using System;
using Swarm.Data;
using Swarm.World;
using Unity.Collections;
using Unity.Mathematics;

namespace Swarm.Agents
{
    /// <summary>
    /// 全部单位（GDD 14.3）+ 空间哈希。双缓冲：并行步读 Agents、写 Next，结算后交换。
    /// 空间哈希是按格子的计数排序（CellStart + Sorted），没有哈希表、没有分配。
    /// </summary>
    public sealed class AgentStore : IDisposable
    {
        public const int CellSize = 2;

        public NativeList<AgentData> Agents;
        internal NativeList<AgentData> Next;
        public readonly int CellsPerSide;
        public NativeArray<int> CellStart;   // [cell + 1]，前缀和
        public NativeList<int> Sorted;       // 按格子排好的单位索引
        int _nextId;

        public int Count => Agents.Length;

        public AgentStore(int mapSize)
        {
            CellsPerSide = (mapSize + CellSize - 1) / CellSize;
            Agents = new NativeList<AgentData>(1024, Allocator.Persistent);
            Next = new NativeList<AgentData>(1024, Allocator.Persistent);
            CellStart = new NativeArray<int>(CellsPerSide * CellsPerSide + 1, Allocator.Persistent);
            Sorted = new NativeList<int>(1024, Allocator.Persistent);
        }

        /// <summary>
        /// 在 center 附近 radius 内的可走格上生成工虫。体型按 mediumFraction 随机；生成后由结算步按权重选资源。
        /// 返回实际生成数量。
        /// </summary>
        public int SpawnWorkers(EnvGrid env, GameBalance b, int count, float2 center, float radius, float mediumFraction, uint worldSeed)
        {
            uint rng = AgentRandom.Seed(worldSeed, _nextId + 7919);
            int made = 0;
            for (int k = 0, guard = 0; k < count && guard < count * 50; guard++)
            {
                float ang = AgentRandom.NextFloat(ref rng) * math.PI * 2f;
                float r = math.sqrt(AgentRandom.NextFloat(ref rng)) * radius;
                float2 p = center + new float2(math.cos(ang), math.sin(ang)) * r;
                int x = (int)math.floor(p.x), y = (int)math.floor(p.y);
                if (!env.InBounds(x, y) || !env.IsGroundWalkable(env.Index(x, y))) continue;
                var tier = AgentRandom.NextFloat(ref rng) < mediumFraction ? SizeTier.Medium : SizeTier.Small;
                Add(new AgentData
                {
                    Position = p,
                    PrevPosition = p,
                    Health = TierOf(b, tier).health,
                    Target = -1,
                    Genome = 0,
                    Rng = AgentRandom.Seed(worldSeed, _nextId),
                    Faction = (byte)Faction.Player,
                    Tier = (byte)tier,
                    State = (byte)AgentState.WorkerSearch,
                    Flags = (byte)(AgentFlags.Alive | AgentFlags.RequestRechoose),
                    WanderAngle = ang,
                });
                k++;
                made++;
            }
            return made;
        }

        static SizeTierSettings TierOf(GameBalance b, SizeTier t)
        {
            var arr = b.sizeTiers != null && b.sizeTiers.Length == 4 ? b.sizeTiers : SizeTierSettings.Defaults();
            return arr[(int)t];
        }

        void Add(AgentData a)
        {
            _nextId++;
            Agents.Add(a);
        }

        internal void PrepareNext()
        {
            if (Next.Length != Agents.Length) Next.ResizeUninitialized(Agents.Length);
            if (Sorted.Length != Agents.Length) Sorted.ResizeUninitialized(Agents.Length);
        }

        internal void Swap() => (Agents, Next) = (Next, Agents);

        public void Dispose()
        {
            if (Agents.IsCreated) Agents.Dispose();
            if (Next.IsCreated) Next.Dispose();
            if (CellStart.IsCreated) CellStart.Dispose();
            if (Sorted.IsCreated) Sorted.Dispose();
        }
    }
}
