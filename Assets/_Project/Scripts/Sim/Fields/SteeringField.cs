using System;
using Swarm.Data;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Swarm.Fields
{
    /// <summary>
    /// 预合成的转向场（GDD 4.5 的性能实现）：信息素场每 5 Hz 才变一次，
    /// 所以每个行为档案（工虫搜索 ×3 种资源、工虫返巢、兵虫待命）在每格只算一次
    /// Σ 权重 × 饱和梯度 + 归巢方向，存成 float2。单位每步只做一次双线性读取，
    /// 成本与单位数量无关地分摊掉 —— 上万只虫子共享同一张场。
    /// 采集 / 集结用远距离吸引场（PaintReach）的单位方向，覆盖内任何位置都能感知涂抹区；
    /// 搜索档案在覆盖范围外额外加回巢拉力（leash），工虫不会漫游出领地。
    /// </summary>
    public sealed class SteeringField : IDisposable
    {
        public const int ProfileSearchProtein = 0, ProfileSearchMineral = 1, ProfileSearchBuild = 2;
        public const int ProfileReturn = 3, ProfileSoldierIdle = 4;
        public const int ProfileCount = 5;

        public readonly int Size, TileCount;
        public NativeArray<float2> Field; // [profile * tiles + tile]
        NativeArray<float> _weights;      // [profile * Stride + channel]，最后一位是归巢权重
        public float LastBuildMs { get; private set; }

        const int Stride = Pheromones.ChannelCount + 1;

        public SteeringField(int size, GameBalance b)
        {
            Size = size;
            TileCount = size * size;
            Field = new NativeArray<float2>(ProfileCount * TileCount, Allocator.Persistent);
            _weights = new NativeArray<float>(ProfileCount * Stride, Allocator.Persistent);
            SetWeights(b);
        }

        /// <summary>写入各档案的通道权重（腺体词条改权重后也调用这里）。</summary>
        public void SetWeights(GameBalance b)
        {
            for (int k = 0; k < 3; k++) Write(k, b.workerSearchWeights, (PheromoneChannel)((int)PheromoneChannel.ScentProtein + k));
            Write(ProfileReturn, b.workerReturnWeights, null);
            Write(ProfileSoldierIdle, b.soldierIdleWeights, null);
        }

        void Write(int profile, SteeringWeights w, PheromoneChannel? scent)
        {
            int o = profile * Stride;
            for (int c = 0; c < Stride; c++) _weights[o + c] = 0f;
            _weights[o + (int)PheromoneChannel.Gather] = w.gather;
            _weights[o + (int)PheromoneChannel.Rally] = w.rally;
            _weights[o + (int)PheromoneChannel.Avoid] = w.avoid;
            _weights[o + (int)PheromoneChannel.Trail] = w.trail;
            _weights[o + (int)PheromoneChannel.Alarm] = w.alarm;
            _weights[o + (int)PheromoneChannel.Danger] = w.danger;
            if (scent.HasValue) _weights[o + (int)scent.Value] = w.scent;
            _weights[o + Pheromones.ChannelCount] = w.homing;
        }

        public void Build(PheromoneFields fields, NativeArray<byte> covered, float saturation, float leash)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            new BuildJob
            {
                size = Size,
                tiles = TileCount,
                saturation = math.max(1e-4f, saturation),
                leash = leash,
                cur = fields.Current,
                reach = fields.PaintReach,
                covered = covered,
                open = fields.Open,
                homing = fields.HomingDistance,
                weights = _weights,
                field = Field,
            }.Schedule(TileCount, 1024).Complete();
            LastBuildMs = (float)sw.Elapsed.TotalMilliseconds;
        }

        /// <summary>双线性读取某档案在 p 处的合力（Tile 中心为采样点）。可在 Burst Job 里调用。</summary>
        public static float2 Sample(NativeArray<float2> field, int size, int profile, float2 p)
        {
            int off = profile * size * size;
            float2 q = p - 0.5f;
            int x0 = math.clamp((int)math.floor(q.x), 0, size - 1), y0 = math.clamp((int)math.floor(q.y), 0, size - 1);
            int x1 = math.min(x0 + 1, size - 1), y1 = math.min(y0 + 1, size - 1);
            float fx = math.saturate(q.x - x0), fy = math.saturate(q.y - y0);
            float2 a = math.lerp(field[off + y0 * size + x0], field[off + y0 * size + x1], fx);
            float2 c = math.lerp(field[off + y1 * size + x0], field[off + y1 * size + x1], fx);
            return math.lerp(a, c, fy);
        }

        public void Dispose()
        {
            if (Field.IsCreated) Field.Dispose();
            if (_weights.IsCreated) _weights.Dispose();
        }

        [BurstCompile(CompileSynchronously = true)]
        struct BuildJob : IJobParallelFor
        {
            public int size, tiles;
            public float saturation, leash;
            [ReadOnly] public NativeArray<float> cur;
            [ReadOnly] public NativeArray<float> reach;
            [ReadOnly] public NativeArray<byte> covered;
            [ReadOnly] public NativeArray<byte> open;
            [ReadOnly] public NativeArray<int> homing;
            [ReadOnly] public NativeArray<float> weights;
            [NativeDisableParallelForRestriction] [WriteOnly] public NativeArray<float2> field;

            public void Execute(int i)
            {
                byte m = open[i];
                if ((m & Pheromones.Walkable) == 0)
                {
                    for (int p = 0; p < ProfileCount; p++) field[p * tiles + i] = 0;
                    return;
                }

                // 各通道的饱和梯度（墙那一侧按本格取值，即单侧差分）
                var g = new FixedGrad();
                for (int c = 0; c < Pheromones.ChannelCount; c++)
                {
                    int off = c * tiles;
                    float v = cur[off + i];
                    float px = (m & Pheromones.OpenPosX) != 0 ? cur[off + i + 1] : v;
                    float nx = (m & Pheromones.OpenNegX) != 0 ? cur[off + i - 1] : v;
                    float py = (m & Pheromones.OpenPosY) != 0 ? cur[off + i + size] : v;
                    float ny = (m & Pheromones.OpenNegY) != 0 ? cur[off + i - size] : v;
                    float2 grad = new float2(px - nx, py - ny) * 0.5f;
                    float len = math.length(grad);
                    g[c] = len > 0f ? grad / (len + saturation) : 0;
                }
                // 采集 / 集结：远距离吸引场的单位方向（离涂抹区越近越接近满强度，进入涂抹区后趋于 0）
                g[(int)PheromoneChannel.Gather] = ReachDir(0, i, m);
                g[(int)PheromoneChannel.Rally] = ReachDir(1, i, m);
                float2 home = HomingDir(i, m);
                bool outside = covered[i] == 0;

                for (int p = 0; p < ProfileCount; p++)
                {
                    int wo = p * Stride;
                    float2 sum = 0;
                    for (int c = 0; c < Pheromones.ChannelCount; c++)
                    {
                        float w = weights[wo + c];
                        if (w != 0f) sum += w * g[c];
                    }
                    sum += weights[wo + Pheromones.ChannelCount] * home;
                    if (outside && p <= ProfileSearchBuild) sum += home * leash;
                    field[p * tiles + i] = sum;
                }
            }

            float2 ReachDir(int k, int i, byte m)
            {
                int off = k * tiles;
                float v = reach[off + i];
                if (v < 0.01f) return 0;
                float px = (m & Pheromones.OpenPosX) != 0 ? reach[off + i + 1] : v;
                float nx = (m & Pheromones.OpenNegX) != 0 ? reach[off + i - 1] : v;
                float py = (m & Pheromones.OpenPosY) != 0 ? reach[off + i + size] : v;
                float ny = (m & Pheromones.OpenNegY) != 0 ? reach[off + i - size] : v;
                float2 grad = new float2(px - nx, py - ny);
                float len = math.length(grad);
                if (len < 1e-6f) return 0;
                // 平台区（涂抹区内部）梯度很小 → 方向强度随之减弱，虫子在区内散开而不是挤向一点
                return grad / len * math.saturate(len / (v * 0.02f + 1e-4f));
            }

            float2 HomingDir(int i, byte m)
            {
                int best = homing[i];
                if (best == PheromoneFields.Unreachable) return 0;
                float2 dir = 0;
                if ((m & Pheromones.OpenPosX) != 0 && homing[i + 1] < best) { best = homing[i + 1]; dir = new float2(1, 0); }
                if ((m & Pheromones.OpenNegX) != 0 && homing[i - 1] < best) { best = homing[i - 1]; dir = new float2(-1, 0); }
                if ((m & Pheromones.OpenPosY) != 0 && homing[i + size] < best) { best = homing[i + size]; dir = new float2(0, 1); }
                if ((m & Pheromones.OpenNegY) != 0 && homing[i - size] < best) { dir = new float2(0, -1); }
                return dir;
            }
        }

        /// <summary>9 个通道梯度的栈上定长数组（Burst 里不能用托管数组）。</summary>
        struct FixedGrad
        {
            float2 g0, g1, g2, g3, g4, g5, g6, g7, g8;

            public float2 this[int k]
            {
                get => k switch { 0 => g0, 1 => g1, 2 => g2, 3 => g3, 4 => g4, 5 => g5, 6 => g6, 7 => g7, _ => g8 };
                set
                {
                    switch (k)
                    {
                        case 0: g0 = value; break; case 1: g1 = value; break; case 2: g2 = value; break;
                        case 3: g3 = value; break; case 4: g4 = value; break; case 5: g5 = value; break;
                        case 6: g6 = value; break; case 7: g7 = value; break; default: g8 = value; break;
                    }
                }
            }
        }
    }
}
