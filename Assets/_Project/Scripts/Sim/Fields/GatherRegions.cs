using System;
using Swarm.Agents;
using Swarm.Data;
using Swarm.World;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Swarm.Fields
{
    /// <summary>
    /// 采集区与拥挤饱和（GDD 4.3）：涂了采集信息素的连通区域是一个采集区。
    /// 容量 = max(保底, 区内可采实体的岗位数) × 笔刷强度（区内最大值）；在区内搜索 / 采集的工虫数接近容量时，
    /// 该区对远处工虫的吸引按 (1 − 人数 / 容量) 减弱，满员即不再吸引，多出来的工虫自然去别处。
    /// 结果是每格一个缩放系数 Scale，远距离吸引场的来源乘以它。1 Hz（以及涂抹变化时）重算。
    /// </summary>
    public sealed class GatherRegions : IDisposable
    {
        public const int MaxRegions = 256;
        public const float PaintThreshold = 0.05f;

        public struct Region
        {
            public int Tiles;
            public float StrengthSum;
            public float StrengthMax;
            public float Slots;
            public int Crowd;
            public float Capacity;
            public float Factor; // 1 = 空闲，0 = 满员
            public float2 Center;
        }

        public readonly int Size, TileCount;
        public NativeArray<int> Label;        // 每格所属采集区，−1 = 无
        public NativeArray<float> Scale;      // 每格吸引缩放
        /// <summary>每格的超员程度 0..1（人数超过容量的比例），转向场据此把多出来的工虫推离区域中心。</summary>
        public NativeArray<float> Overflow;
        public NativeArray<Region> Regions;
        NativeArray<int> _count;              // [0] = 区数
        public float LastBuildMs { get; private set; }
        public int Count => _count[0];

        public GatherRegions(int size)
        {
            Size = size;
            TileCount = size * size;
            Label = new NativeArray<int>(TileCount, Allocator.Persistent);
            Scale = new NativeArray<float>(TileCount, Allocator.Persistent);
            Overflow = new NativeArray<float>(TileCount, Allocator.Persistent);
            Regions = new NativeArray<Region>(MaxRegions, Allocator.Persistent);
            _count = new NativeArray<int>(1, Allocator.Persistent);
        }

        public void Build(PheromoneFields fields, ResourceField resources, ResourceLookup lookup, AgentStore agents, GameBalance b)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            new BuildJob
            {
                size = Size,
                tiles = TileCount,
                paint = fields.PaintLayer.GetSubArray((int)PheromoneChannel.Gather * TileCount, TileCount),
                open = fields.Open,
                nodes = resources.Nodes.AsArray(),
                info = lookup.Info,
                agents = agents.Agents.AsArray(),
                slotsNormal = b.gatherSlotsPerNode,
                slotsGiant = b.gatherSlotsPerGiant,
                minSlots = b.gatherMinSlots,
                label = Label,
                scale = Scale,
                overflow = Overflow,
                regions = Regions,
                count = _count,
            }.Run();
            LastBuildMs = (float)sw.Elapsed.TotalMilliseconds;
        }

        public bool TryGetRegionAt(int tile, out Region region)
        {
            int l = Label[tile];
            region = l >= 0 ? Regions[l] : default;
            return l >= 0;
        }

        public void Dispose()
        {
            if (Label.IsCreated) Label.Dispose();
            if (Scale.IsCreated) Scale.Dispose();
            if (Overflow.IsCreated) Overflow.Dispose();
            if (Regions.IsCreated) Regions.Dispose();
            if (_count.IsCreated) _count.Dispose();
        }

        [BurstCompile(CompileSynchronously = true)]
        struct BuildJob : IJob
        {
            public int size, tiles;
            [ReadOnly] public NativeArray<float> paint;
            [ReadOnly] public NativeArray<byte> open;
            [ReadOnly] public NativeArray<ResourceNode> nodes;
            [ReadOnly] public NativeArray<NodeInfo> info;
            [ReadOnly] public NativeArray<AgentData> agents;
            public float slotsNormal, slotsGiant, minSlots;
            public NativeArray<int> label;
            public NativeArray<float> scale;
            public NativeArray<float> overflow;
            public NativeArray<Region> regions;
            public NativeArray<int> count;

            public void Execute()
            {
                // 不属于任何采集区的格子（笔刷边缘极淡的一圈）不产生远距离吸引，否则满员后仍会漏出吸引
                for (int i = 0; i < tiles; i++) { label[i] = -1; scale[i] = 0f; overflow[i] = 0f; }
                int n = 0;
                var queue = new NativeQueue<int>(Allocator.Temp);

                // 1) 连通区标记（4 邻接，遵守墙）
                for (int i = 0; i < tiles && n < MaxRegions; i++)
                {
                    if (label[i] >= 0 || paint[i] < PaintThreshold) continue;
                    var r = new Region();
                    label[i] = n;
                    queue.Enqueue(i);
                    float2 sum = 0;
                    while (queue.TryDequeue(out int t))
                    {
                        r.Tiles++;
                        r.StrengthSum += paint[t];
                        r.StrengthMax = math.max(r.StrengthMax, paint[t]);
                        sum += new float2(t % size, t / size) + 0.5f;
                        byte m = open[t];
                        if ((m & Pheromones.OpenPosX) != 0) Visit(t + 1, n, queue);
                        if ((m & Pheromones.OpenNegX) != 0) Visit(t - 1, n, queue);
                        if ((m & Pheromones.OpenPosY) != 0) Visit(t + size, n, queue);
                        if ((m & Pheromones.OpenNegY) != 0) Visit(t - size, n, queue);
                    }
                    r.Center = sum / math.max(1, r.Tiles);
                    regions[n] = r;
                    n++;
                }
                queue.Dispose();
                count[0] = n;
                if (n == 0) return;

                // 2) 岗位：区内（或紧挨着，巨型实体占地本身不可涂）的可采实体
                for (int k = 0; k < nodes.Length; k++)
                {
                    var node = nodes[k];
                    if (!ResourceLookup.Harvestable(node)) continue;
                    int fp = info[node.Def].Footprint;
                    int reg = LabelNear(node.Position, fp > 1 ? (int)math.ceil(fp * 0.5f) + 1 : 1);
                    if (reg < 0) continue;
                    var r = regions[reg];
                    r.Slots += fp > 1 ? slotsGiant : slotsNormal;
                    regions[reg] = r;
                }

                // 3) 拥挤：区内正在搜索 / 采集的玩家工虫
                for (int a = 0; a < agents.Length; a++)
                {
                    var ag = agents[a];
                    if (!ag.IsAlive || ag.Faction != (byte)Faction.Player || ag.Layer != 0) continue;
                    if (ag.State != (byte)AgentState.WorkerSearch && ag.State != (byte)AgentState.WorkerHarvest) continue;
                    int x = math.clamp((int)ag.Position.x, 0, size - 1), y = math.clamp((int)ag.Position.y, 0, size - 1);
                    int reg = label[y * size + x];
                    if (reg < 0) continue;
                    var r = regions[reg];
                    r.Crowd++;
                    regions[reg] = r;
                }

                // 4) 容量与吸引系数
                for (int k = 0; k < n; k++)
                {
                    var r = regions[k];
                    float strength = r.StrengthMax; // 玩家选的笔刷强度（边缘柔化不算）
                    r.Capacity = math.max(1f, math.max(minSlots, r.Slots) * strength);
                    r.Factor = math.saturate(1f - r.Crowd / r.Capacity);
                    regions[k] = r;
                }
                for (int i = 0; i < tiles; i++)
                {
                    int l = label[i];
                    if (l < 0) continue;
                    var r = regions[l];
                    scale[i] = r.Factor;
                    overflow[i] = math.saturate(r.Crowd / r.Capacity - 1f);
                }
            }

            void Visit(int t, int n, NativeQueue<int> q)
            {
                if (label[t] >= 0 || paint[t] < PaintThreshold) return;
                label[t] = n;
                q.Enqueue(t);
            }

            int LabelNear(float2 p, int r)
            {
                int cx = (int)math.floor(p.x), cy = (int)math.floor(p.y);
                int c = Lbl(cx, cy);
                if (c >= 0) return c;
                for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    int l = Lbl(cx + dx, cy + dy);
                    if (l >= 0) return l;
                }
                return -1;
            }

            int Lbl(int x, int y) => (uint)x < (uint)size && (uint)y < (uint)size ? label[y * size + x] : -1;
        }
    }
}
