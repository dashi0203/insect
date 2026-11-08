using System;
using System.Collections.Generic;
using System.Diagnostics;
using Swarm.Data;
using Swarm.World;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Swarm.Fields
{
    public static class Pheromones
    {
        public const int ChannelCount = 9;

        // Open 掩码：向 4 个邻居的扩散是否开放，以及本 Tile 是否可行走
        public const byte OpenPosX = 1, OpenNegX = 2, OpenPosY = 4, OpenNegY = 8, Walkable = 16;

        public static bool IsPlayer(PheromoneChannel c) => c <= PheromoneChannel.Avoid;
        public static bool IsEnvironment(PheromoneChannel c) => c >= PheromoneChannel.ScentProtein;

        public static PheromoneChannel ScentFor(ResourceKind kind) => kind switch
        {
            ResourceKind.Protein => PheromoneChannel.ScentProtein,
            ResourceKind.Mineral => PheromoneChannel.ScentMineral,
            _ => PheromoneChannel.ScentBuild,
        };
    }

    public struct ChannelParams
    {
        public float Diffusion, DecayInside, DecayOutside, Approach, Cap;
        public byte HasTarget; // 有目标层：玩家绘制层或环境重算值
        public byte IsPaint;   // 目标层是玩家绘制层（覆盖外会逐渐消散）
    }

    /// <summary>
    /// 地表信息素场（GDD 4.1 / 4.2）。全部通道存在一个连续数组里：索引 = 通道 × TileCount + Tile。
    /// 5 Hz：一个 Burst 并行 Job 更新全部通道（扩散 + 衰减 + 来源 + 向目标层靠拢），双缓冲。
    /// 1 Hz：环境通道（资源气味 ×3、危险）的目标层按来源重算，并按距离衰减传播（墙阻挡）。
    /// 归巢：从巢口出发的 BFS 距离，地形 / 建筑变化时重算。
    /// 不可行走 Tile、以及没有坡道的悬崖边缘视为墙，阻挡扩散。
    /// </summary>
    public sealed class PheromoneFields : IDisposable
    {
        public const int Unreachable = int.MaxValue;
        const int C = Pheromones.ChannelCount;

        public readonly int Size;
        public readonly int TileCount;

        NativeArray<float> _cur, _next, _target, _source;
        NativeArray<ChannelParams> _params;
        NativeArray<float> _falloff; // 环境通道的每格衰减系数
        public NativeArray<byte> Open;
        public NativeArray<int> HomingDistance;

        /// <summary>当前场（只读视图给 Job 用；每次 Step 后换成新缓冲，所以每步重新取）。</summary>
        public NativeArray<float> Current => _cur;
        /// <summary>一次性来源（路径、警报），下一次 Step 加到场上。单线程结算 Job 直接写入。</summary>
        public NativeArray<float> Source => _source;

        /// <summary>
        /// 采集 / 集结笔刷的远距离吸引（[0] 采集，[1] 集结）：绘制层按距离衰减传播（墙阻挡），
        /// 让离涂抹区很远的虫子也能知道往哪走；局部场本身扩散很慢，只在近处起作用。
        /// </summary>
        public NativeArray<float> PaintReach => _reach;
        NativeArray<float> _reach, _reachFalloff;
        bool _reachDirty = true;

        /// <summary>玩家绘制层（目标层的前三个通道）。</summary>
        public NativeArray<float> PaintLayer => _target;
        /// <summary>自上次重算远距离吸引后，绘制层是否被涂抹 / 擦除过。</summary>
        public bool PaintDirty => _reachDirty;
        /// <summary>上次重算时是否存在任何采集 / 集结涂抹（没有时 1 Hz 刷新整个跳过）。</summary>
        public bool HasPaint { get; private set; }
        public float LastReachMs { get; private set; }

        public float LastStepMs { get; private set; }
        public float LastEnvironmentMs { get; private set; }

        readonly float _scentFull;
        readonly float _scentInfinite;

        public PheromoneFields(EnvGrid env, GameBalance b)
        {
            Size = env.Size;
            TileCount = env.Length;
            int total = C * TileCount;
            const Allocator a = Allocator.Persistent;
            _cur = new NativeArray<float>(total, a);
            _next = new NativeArray<float>(total, a);
            _target = new NativeArray<float>(total, a);
            _source = new NativeArray<float>(total, a);
            Open = new NativeArray<byte>(TileCount, a);
            HomingDistance = new NativeArray<int>(TileCount, a);
            _params = new NativeArray<ChannelParams>(C, a);
            _falloff = new NativeArray<float>(C, a);
            _reach = new NativeArray<float>(2 * TileCount, a);
            _reachFalloff = new NativeArray<float>(2, a);
            _reachFalloff[0] = _reachFalloff[1] = math.exp(-1f / math.max(1f, b.paintReachRange));

            for (int c = 0; c < C; c++)
            {
                var ch = (PheromoneChannel)c;
                var s = Find(b.pheromoneChannels, ch);
                bool player = Pheromones.IsPlayer(ch);
                bool envCh = Pheromones.IsEnvironment(ch);
                _params[c] = new ChannelParams
                {
                    Diffusion = s.diffusion,
                    DecayInside = s.decayInside,
                    DecayOutside = s.decayOutside,
                    Approach = s.targetApproach,
                    Cap = s.cap > 0f ? s.cap : float.MaxValue,
                    HasTarget = (byte)(player || envCh ? 1 : 0),
                    IsPaint = (byte)(player ? 1 : 0),
                };
                float range = ch == PheromoneChannel.Danger ? b.dangerRange : b.scentRange;
                _falloff[c] = math.exp(-1f / math.max(0.5f, range));
            }
            _scentFull = math.max(1f, b.scentFullAmount);
            _scentInfinite = b.scentInfiniteValue;
            for (int i = 0; i < TileCount; i++) HomingDistance[i] = Unreachable;
            BuildOpenMask(env);
        }

        static PheromoneChannelSettings Find(PheromoneChannelSettings[] arr, PheromoneChannel c)
        {
            if (arr != null)
                foreach (var s in arr)
                    if (s.channel == c) return s;
            return GameBalance.DefaultPheromoneChannels()[(int)c];
        }

        // ---------------------------------------------------------------- walls

        /// <summary>重算墙体：可行走性、相邻 Tile 之间能否扩散（悬崖边需有坡道）。地形或巨型实体变化后调用。</summary>
        public void BuildOpenMask(EnvGrid env)
        {
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                int i = env.Index(x, y);
                if (!env.IsGroundWalkable(i)) { Open[i] = 0; continue; }
                byte m = Pheromones.Walkable;
                if (x + 1 < Size && Passable(env, i, i + 1)) m |= Pheromones.OpenPosX;
                if (x - 1 >= 0 && Passable(env, i, i - 1)) m |= Pheromones.OpenNegX;
                if (y + 1 < Size && Passable(env, i, i + Size)) m |= Pheromones.OpenPosY;
                if (y - 1 >= 0 && Passable(env, i, i - Size)) m |= Pheromones.OpenNegY;
                Open[i] = m;
            }
        }

        static bool Passable(EnvGrid env, int a, int b)
        {
            if (!env.IsGroundWalkable(b)) return false;
            if (env.HeightLevel[a] != env.HeightLevel[b]) return env.IsRamp(a) || env.IsRamp(b);
            return true;
        }

        // ---------------------------------------------------------------- 5 Hz step

        /// <summary>全部通道走一步（dt 秒）。covered：覆盖范围（决定衰减率与绘制层消散）。</summary>
        public void Step(float dt, NativeArray<byte> covered)
        {
            var sw = Stopwatch.StartNew();
            new DiffuseJob
            {
                size = Size,
                tiles = TileCount,
                dt = dt,
                cur = _cur,
                next = _next,
                target = _target,
                source = _source,
                channels = _params,
                open = Open,
                covered = covered,
            }.Schedule(C * TileCount, 4096).Complete();
            (_cur, _next) = (_next, _cur);
            LastStepMs = (float)sw.Elapsed.TotalMilliseconds;
        }

        // ---------------------------------------------------------------- player paint

        /// <summary>
        /// 笔刷（GDD 4.3）：只在覆盖内生效；强度按半径平滑衰减；取最大值，重复涂抹不累加。
        /// 返回实际写入的 Tile 数。
        /// </summary>
        public int Paint(PheromoneChannel c, float2 center, float radius, float strength, NativeArray<byte> covered)
        {
            if (!Pheromones.IsPlayer(c)) return 0;
            int off = (int)c * TileCount;
            int painted = 0;
            ForDisc(center, radius, (i, d) =>
            {
                if (covered[i] == 0 || (Open[i] & Pheromones.Walkable) == 0) return;
                float w = radius <= 1f ? strength : strength * (1f - math.smoothstep(radius * 0.35f, radius, d));
                if (w <= 0f) return;
                if (w > _target[off + i]) _target[off + i] = w;
                painted++;
            });
            if (painted > 0) _reachDirty = true;
            return painted;
        }

        /// <summary>擦除：清空半径内三个玩家通道的绘制层（场随后自然回落）。</summary>
        public void Erase(float2 center, float radius)
        {
            ForDisc(center, radius, (i, d) =>
            {
                for (int c = 0; c <= (int)PheromoneChannel.Avoid; c++) _target[c * TileCount + i] = 0f;
            });
            _reachDirty = true;
        }

        void ForDisc(float2 center, float radius, Action<int, float> visit)
        {
            int r = (int)math.ceil(radius);
            int cx = (int)math.floor(center.x), cy = (int)math.floor(center.y);
            for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++)
            {
                int x = cx + dx, y = cy + dy;
                if ((uint)x >= (uint)Size || (uint)y >= (uint)Size) continue;
                float d = math.distance(new float2(x + 0.5f, y + 0.5f), center);
                if (d > radius) continue;
                visit(y * Size + x, d);
            }
        }

        /// <summary>来源（路径、警报）：下一次 Step 时一次性加到场上，随后受上限约束。</summary>
        public void Deposit(PheromoneChannel c, int tile, float amount) => _source[(int)c * TileCount + tile] += amount;

        public float Get(PheromoneChannel c, int tile) => _cur[(int)c * TileCount + tile];
        public float GetPaint(PheromoneChannel c, int tile) => _target[(int)c * TileCount + tile];

        public float Total(PheromoneChannel c)
        {
            float sum = 0f;
            int off = (int)c * TileCount;
            for (int i = 0; i < TileCount; i++) sum += _cur[off + i];
            return sum;
        }

        // ---------------------------------------------------------------- 1 Hz environment

        /// <summary>资源气味 ×3（按剩余储量）与危险（毒性）的目标层重算 + 距离衰减传播。</summary>
        public void RecomputeEnvironment(EnvGrid env, ResourceField resources)
        {
            var sw = Stopwatch.StartNew();
            int first = (int)PheromoneChannel.ScentProtein;
            for (int c = first; c < C; c++)
            {
                int off = c * TileCount;
                for (int i = 0; i < TileCount; i++) _target[off + i] = 0f;
            }

            if (resources != null && resources.Nodes.IsCreated)
            {
                float logFull = math.log(1f + _scentFull);
                for (int n = 0; n < resources.Nodes.Length; n++)
                {
                    var node = resources.Nodes[n];
                    if (node.Alive == 0) continue;
                    float v = node.Infinite != 0
                        ? _scentInfinite
                        : math.saturate(math.log(1f + math.max(0f, node.Amount)) / logFull);
                    if (v <= 0f) continue;
                    int off = (int)Pheromones.ScentFor(resources.Defs[node.Def].kind) * TileCount;

                    if (node.Footprint <= 1)
                    {
                        int i = ResourceField.TileIndex(env, node.Position);
                        if (v > _target[off + i]) _target[off + i] = v;
                        continue;
                    }
                    // 巨型实体占地不可走：从外圈一圈可走 Tile 散发
                    int f = node.Footprint;
                    int x0 = (int)math.floor(node.Position.x - f * 0.5f) - 1, y0 = (int)math.floor(node.Position.y - f * 0.5f) - 1;
                    for (int y = y0; y <= y0 + f + 1; y++)
                    for (int x = x0; x <= x0 + f + 1; x++)
                    {
                        if (!env.InBounds(x, y)) continue;
                        int i = env.Index(x, y);
                        if ((Open[i] & Pheromones.Walkable) == 0) continue;
                        if (v > _target[off + i]) _target[off + i] = v;
                    }
                }
            }

            int dangerOff = (int)PheromoneChannel.Danger * TileCount;
            for (int i = 0; i < TileCount; i++)
            {
                float tox = env.Toxicity[i];
                if (tox > 0.01f) _target[dangerOff + i] = tox;
            }

            new PropagateJob
            {
                size = Size,
                tiles = TileCount,
                firstChannel = first,
                field = _target,
                open = Open,
                falloff = _falloff,
            }.Schedule(C - first, 1).Complete();
            LastEnvironmentMs = (float)sw.Elapsed.TotalMilliseconds;
        }

        // ---------------------------------------------------------------- paint reach

        /// <summary>
        /// 重算远距离吸引场。gatherScale：每格的采集吸引缩放（采集区拥挤饱和），为空则不缩放。
        /// </summary>
        public void RebuildPaintReach(NativeArray<float> gatherScale)
        {
            var sw = Stopwatch.StartNew();
            _reachDirty = false;
            bool scaled = gatherScale.IsCreated;
            bool any = false;
            int g = (int)PheromoneChannel.Gather * TileCount, r = (int)PheromoneChannel.Rally * TileCount;
            for (int i = 0; i < TileCount; i++)
            {
                float pg = _target[g + i], pr = _target[r + i];
                any |= pg > 0f || pr > 0f;
                _reach[i] = scaled ? pg * gatherScale[i] : pg;
                _reach[TileCount + i] = pr;
            }
            HasPaint = any;
            new PropagateJob
            {
                size = Size,
                tiles = TileCount,
                firstChannel = 0,
                field = _reach,
                open = Open,
                falloff = _reachFalloff,
            }.Schedule(2, 1).Complete();
            LastReachMs = (float)sw.Elapsed.TotalMilliseconds;
        }

        // ---------------------------------------------------------------- homing

        /// <summary>归巢距离场：从所有入口出发的 BFS（4 邻接，遵守墙体）。</summary>
        public void RecomputeHoming(IEnumerable<int2> entrances)
        {
            for (int i = 0; i < TileCount; i++) HomingDistance[i] = Unreachable;
            var q = new Queue<int>();
            foreach (var e in entrances)
            {
                if ((uint)e.x >= (uint)Size || (uint)e.y >= (uint)Size) continue;
                int i = e.y * Size + e.x;
                HomingDistance[i] = 0;
                q.Enqueue(i);
            }
            while (q.Count > 0)
            {
                int t = q.Dequeue();
                int nd = HomingDistance[t] + 1;
                byte m = Open[t];
                if ((m & Pheromones.OpenPosX) != 0) Visit(t + 1, nd, q);
                if ((m & Pheromones.OpenNegX) != 0) Visit(t - 1, nd, q);
                if ((m & Pheromones.OpenPosY) != 0) Visit(t + Size, nd, q);
                if ((m & Pheromones.OpenNegY) != 0) Visit(t - Size, nd, q);
            }
        }

        void Visit(int i, int d, Queue<int> q)
        {
            if (HomingDistance[i] <= d) return;
            HomingDistance[i] = d;
            q.Enqueue(i);
        }

        /// <summary>归巢方向：指向 8 邻居中距离最小者（单位向量），已在巢口或不可达时为 0。</summary>
        public float2 HomingDirection(float2 p)
        {
            int x = math.clamp((int)math.floor(p.x), 0, Size - 1), y = math.clamp((int)math.floor(p.y), 0, Size - 1);
            int best = HomingDistance[y * Size + x];
            float2 dir = 0;
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                int nx = x + dx, ny = y + dy;
                if ((uint)nx >= (uint)Size || (uint)ny >= (uint)Size) continue;
                int d = HomingDistance[ny * Size + nx];
                if (d < best) { best = d; dir = new float2(dx, dy); }
            }
            return math.normalizesafe(dir);
        }

        // ---------------------------------------------------------------- sampling (agents, M3)

        /// <summary>双线性采样（Tile 中心为采样点）。</summary>
        public float Sample(PheromoneChannel c, float2 p)
        {
            int off = (int)c * TileCount;
            float2 q = p - 0.5f;
            int x0 = math.clamp((int)math.floor(q.x), 0, Size - 1), y0 = math.clamp((int)math.floor(q.y), 0, Size - 1);
            int x1 = math.min(x0 + 1, Size - 1), y1 = math.min(y0 + 1, Size - 1);
            float fx = math.saturate(q.x - x0), fy = math.saturate(q.y - y0);
            float v00 = _cur[off + y0 * Size + x0], v10 = _cur[off + y0 * Size + x1];
            float v01 = _cur[off + y1 * Size + x0], v11 = _cur[off + y1 * Size + x1];
            return math.lerp(math.lerp(v00, v10, fx), math.lerp(v01, v11, fx), fy);
        }

        public float2 Gradient(PheromoneChannel c, float2 p)
        {
            const float h = 0.5f;
            return new float2(
                Sample(c, p + new float2(h, 0)) - Sample(c, p - new float2(h, 0)),
                Sample(c, p + new float2(0, h)) - Sample(c, p - new float2(0, h))) / (2f * h);
        }

        // ---------------------------------------------------------------- overlay packing

        /// <summary>
        /// 打包热力图纹理：A = 采集 / 集结 / 回避 / 路径，B = 警报 / 三种气味，C = 危险 / 覆盖 / 归巢 / 0。
        /// 各通道除以显示满值，Shader 里直接 saturate。
        /// </summary>
        public void Pack(NativeArray<half4> a, NativeArray<half4> b, NativeArray<half4> c,
            NativeArray<byte> covered, float4 invScaleA, float4 invScaleB, float invDanger, float homingRange)
        {
            new PackJob
            {
                tiles = TileCount,
                cur = _cur,
                covered = covered,
                homing = HomingDistance,
                invScaleA = invScaleA,
                invScaleB = invScaleB,
                invDanger = invDanger,
                homingRange = math.max(1f, homingRange),
                a = a, b = b, c = c,
            }.Schedule(TileCount, 4096).Complete();
        }

        public void Dispose()
        {
            if (_cur.IsCreated) _cur.Dispose();
            if (_next.IsCreated) _next.Dispose();
            if (_target.IsCreated) _target.Dispose();
            if (_source.IsCreated) _source.Dispose();
            if (_params.IsCreated) _params.Dispose();
            if (_falloff.IsCreated) _falloff.Dispose();
            if (_reach.IsCreated) _reach.Dispose();
            if (_reachFalloff.IsCreated) _reachFalloff.Dispose();
            if (Open.IsCreated) Open.Dispose();
            if (HomingDistance.IsCreated) HomingDistance.Dispose();
        }

        // ---------------------------------------------------------------- jobs

        /// <summary>
        /// C' = C + D(Σ开放邻居 (Cj − Ci))Δt − k·C·Δt + S + a·(T − C)·Δt，夹到 [0, cap]。
        /// 墙两侧不交换（只对开放邻居求和），因此墙阻挡扩散且总量守恒。
        /// </summary>
        [BurstCompile(CompileSynchronously = true)]
        struct DiffuseJob : IJobParallelFor
        {
            public int size, tiles;
            public float dt;
            [ReadOnly] public NativeArray<float> cur;
            [WriteOnly] public NativeArray<float> next;
            public NativeArray<float> target;
            public NativeArray<float> source;
            [ReadOnly] public NativeArray<ChannelParams> channels;
            [ReadOnly] public NativeArray<byte> open;
            [ReadOnly] public NativeArray<byte> covered;

            public void Execute(int idx)
            {
                int c = idx / tiles;
                int i = idx - c * tiles;
                byte m = open[i];
                if ((m & Pheromones.Walkable) == 0)
                {
                    next[idx] = 0f;
                    source[idx] = 0f;
                    return;
                }

                var p = channels[c];
                float v = cur[idx];
                float lap = 0f;
                if ((m & Pheromones.OpenPosX) != 0) lap += cur[idx + 1] - v;
                if ((m & Pheromones.OpenNegX) != 0) lap += cur[idx - 1] - v;
                if ((m & Pheromones.OpenPosY) != 0) lap += cur[idx + size] - v;
                if ((m & Pheromones.OpenNegY) != 0) lap += cur[idx - size] - v;

                bool cov = covered[i] != 0;
                float k = cov ? p.DecayInside : p.DecayOutside;
                float nv = v + p.Diffusion * lap * dt - k * v * dt + source[idx];
                source[idx] = 0f;

                if (p.HasTarget != 0)
                {
                    float t = target[idx];
                    if (p.IsPaint != 0 && !cov && t > 0f)
                    {
                        t -= p.DecayOutside * t * dt; // 失去覆盖后，绘制层逐渐消散
                        target[idx] = t;
                    }
                    nv += (t - nv) * math.min(1f, p.Approach * dt);
                }
                next[idx] = math.clamp(nv, 0f, p.Cap);
            }
        }

        /// <summary>
        /// 环境通道的距离衰减传播：双向光栅扫描（含对角），v = max(v, 邻居 × 衰减)，只穿过开放的边。跑两轮以绕过墙角。
        /// 每个通道一个并行任务。
        /// </summary>
        [BurstCompile(CompileSynchronously = true)]
        struct PropagateJob : IJobParallelFor
        {
            public int size, tiles, firstChannel;
            [NativeDisableParallelForRestriction] public NativeArray<float> field;
            [ReadOnly] public NativeArray<byte> open;
            [ReadOnly] public NativeArray<float> falloff;

            public void Execute(int k)
            {
                int c = firstChannel + k;
                int off = c * tiles;
                float f = falloff[c];
                float fd = math.pow(f, 1.41421356f);

                for (int round = 0; round < 2; round++)
                {
                    for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        int i = y * size + x;
                        byte m = open[i];
                        if ((m & Pheromones.Walkable) == 0) continue;
                        float v = field[off + i];
                        if ((m & Pheromones.OpenNegX) != 0) v = math.max(v, field[off + i - 1] * f);
                        if ((m & Pheromones.OpenNegY) != 0)
                        {
                            v = math.max(v, field[off + i - size] * f);
                            byte below = open[i - size];
                            if ((m & Pheromones.OpenNegX) != 0 && (below & Pheromones.OpenNegX) != 0)
                                v = math.max(v, field[off + i - size - 1] * fd);
                            if ((m & Pheromones.OpenPosX) != 0 && (below & Pheromones.OpenPosX) != 0)
                                v = math.max(v, field[off + i - size + 1] * fd);
                        }
                        field[off + i] = v;
                    }
                    for (int y = size - 1; y >= 0; y--)
                    for (int x = size - 1; x >= 0; x--)
                    {
                        int i = y * size + x;
                        byte m = open[i];
                        if ((m & Pheromones.Walkable) == 0) continue;
                        float v = field[off + i];
                        if ((m & Pheromones.OpenPosX) != 0) v = math.max(v, field[off + i + 1] * f);
                        if ((m & Pheromones.OpenPosY) != 0)
                        {
                            v = math.max(v, field[off + i + size] * f);
                            byte above = open[i + size];
                            if ((m & Pheromones.OpenPosX) != 0 && (above & Pheromones.OpenPosX) != 0)
                                v = math.max(v, field[off + i + size + 1] * fd);
                            if ((m & Pheromones.OpenNegX) != 0 && (above & Pheromones.OpenNegX) != 0)
                                v = math.max(v, field[off + i + size - 1] * fd);
                        }
                        field[off + i] = v;
                    }
                }
            }
        }

        [BurstCompile(CompileSynchronously = true)]
        struct PackJob : IJobParallelFor
        {
            public int tiles;
            [ReadOnly] public NativeArray<float> cur;
            [ReadOnly] public NativeArray<byte> covered;
            [ReadOnly] public NativeArray<int> homing;
            public float4 invScaleA, invScaleB;
            public float invDanger, homingRange;
            [WriteOnly] public NativeArray<half4> a, b, c;

            public void Execute(int i)
            {
                int n = tiles;
                a[i] = new half4(new float4(cur[i], cur[n + i], cur[2 * n + i], cur[3 * n + i]) * invScaleA);
                b[i] = new half4(new float4(cur[4 * n + i], cur[5 * n + i], cur[6 * n + i], cur[7 * n + i]) * invScaleB);
                int d = homing[i];
                float h = d == Unreachable ? 0f : math.saturate(1f - d / homingRange);
                c[i] = new half4(new float4(cur[8 * n + i] * invDanger, covered[i] != 0 ? 1f : 0f, h, 0f));
            }
        }
    }
}
