using System;
using Swarm.Data;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Swarm.World
{
    /// <summary>资源实体的静态信息（Job 用，避免在 Burst 里访问 ScriptableObject）。</summary>
    public struct NodeInfo
    {
        public byte Kind;          // ResourceKind
        public byte Footprint;
        public float HarvestRate;  // 单只工虫每秒采集量
    }

    /// <summary>
    /// 每格“可采集实体”索引：对每种资源，记录站在该格时采集范围内的一个实体（−1 = 无）。
    /// 工虫每步 O(1) 查询自己所在格，再用实体的实时储量确认是否可采。1 Hz 随资源结算重建。
    /// </summary>
    public sealed class ResourceLookup : IDisposable
    {
        public readonly int Size, TileCount;
        public NativeArray<int> NodeAt;      // [kind * tiles + tile]
        public NativeArray<NodeInfo> Info;   // [def]
        public float LastBuildMs { get; private set; }

        public ResourceLookup(int size, ResourceNodeDef[] defs)
        {
            Size = size;
            TileCount = size * size;
            NodeAt = new NativeArray<int>(3 * TileCount, Allocator.Persistent);
            Info = new NativeArray<NodeInfo>(defs.Length, Allocator.Persistent);
            for (int d = 0; d < defs.Length; d++)
                Info[d] = new NodeInfo
                {
                    Kind = (byte)defs[d].kind,
                    Footprint = (byte)math.max(1, defs[d].footprint),
                    HarvestRate = math.max(0.01f, defs[d].harvestRatePerSecond),
                };
        }

        public void Build(ResourceField resources, float harvestRange)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            new BuildJob
            {
                size = Size,
                tiles = TileCount,
                range = harvestRange,
                nodes = resources.Nodes.AsArray(),
                info = Info,
                nodeAt = NodeAt,
            }.Run();
            LastBuildMs = (float)sw.Elapsed.TotalMilliseconds;
        }

        public static bool Harvestable(in ResourceNode n) => n.Alive != 0 && (n.Infinite != 0 || n.Amount > 0f);

        public void Dispose()
        {
            if (NodeAt.IsCreated) NodeAt.Dispose();
            if (Info.IsCreated) Info.Dispose();
        }

        [BurstCompile(CompileSynchronously = true)]
        struct BuildJob : IJob
        {
            public int size, tiles;
            public float range;
            [ReadOnly] public NativeArray<ResourceNode> nodes;
            [ReadOnly] public NativeArray<NodeInfo> info;
            public NativeArray<int> nodeAt;

            public void Execute()
            {
                for (int i = 0; i < nodeAt.Length; i++) nodeAt[i] = -1;
                for (int n = 0; n < nodes.Length; n++)
                {
                    var node = nodes[n];
                    if (!Harvestable(node)) continue;
                    var inf = info[node.Def];
                    float r = range + (inf.Footprint > 1 ? inf.Footprint * 0.5f : 0f);
                    int off = inf.Kind * tiles;
                    int ri = (int)math.ceil(r);
                    int cx = (int)math.floor(node.Position.x), cy = (int)math.floor(node.Position.y);
                    float r2 = r * r;
                    for (int dy = -ri; dy <= ri; dy++)
                    for (int dx = -ri; dx <= ri; dx++)
                    {
                        int x = cx + dx, y = cy + dy;
                        if ((uint)x >= (uint)size || (uint)y >= (uint)size) continue;
                        if (math.distancesq(new float2(x + 0.5f, y + 0.5f), node.Position) > r2) continue;
                        nodeAt[off + y * size + x] = n;
                    }
                }
            }
        }
    }
}
