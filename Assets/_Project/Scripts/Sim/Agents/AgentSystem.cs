using System;
using Swarm.Core;
using Swarm.Data;
using Swarm.Fields;
using Unity.Collections;
using Unity.Jobs;

namespace Swarm.Agents
{
    /// <summary>
    /// 10 Hz 单位系统（GDD 4.5 / 4.6 / 14.2）：空间哈希 → 并行步（感知、状态、转向、移动）→ 单线程结算。
    /// 三个 Job 串成一条依赖链一次提交；所有缓冲常驻，每步零托管分配。
    /// </summary>
    public sealed class AgentSystem : ISimSystem, IDisposable
    {
        NativeArray<TierParams> _tiers;
        NativeArray<byte> _dirtyChunks;
        BehaviourParams _params;

        public string Name => "Agents";

        public AgentSystem(SimulationWorld w)
        {
            var b = w.Balance;
            var src = b.sizeTiers != null && b.sizeTiers.Length == 4 ? b.sizeTiers : SizeTierSettings.Defaults();
            _tiers = new NativeArray<TierParams>(src.Length, Allocator.Persistent);
            for (int k = 0; k < src.Length; k++)
                _tiers[k] = new TierParams { Speed = src[k].speed, Carry = src[k].carry, Health = src[k].health };
            _dirtyChunks = new NativeArray<byte>(w.Resources.ChunkRanges.Length, Allocator.Persistent);
            _params = new BehaviourParams
            {
                HarvestRange = b.harvestRange,
                HarvestSeconds = b.harvestSeconds,
                GiveUpSeconds = b.searchGiveUpSeconds,
                Wander = b.wanderStrength,
                WanderTurn = b.wanderTurnRate,
                SeparationRadius = b.separationRadius,
                SeparationStrength = b.separationStrength,
                Response = b.steeringResponse,
                ColdStop = b.coldStopTemperature,
                ColdMultiplier = b.coldMoveMultiplier,
                FleeHealth = b.workerFleeHealth,
                FleeAlarm = b.workerFleeAlarm,
                UnloadDistance = b.unloadHomingDistance,
                GatherOverride = b.gatherOverrideThreshold,
            };
        }

        public void Tick(SimulationWorld w, float dt)
        {
            var store = w.Agents;
            if (store.Count == 0) return;
            store.PrepareNext();
            var cur = store.Agents.AsArray();
            var next = store.Next.AsArray();
            int size = w.Env.Size, tiles = w.Env.Length;
            var field = w.Pheromones.Current;

            var hash = new BuildHashJob
            {
                cellsPerSide = store.CellsPerSide,
                cellSize = AgentStore.CellSize,
                agents = cur,
                cellStart = store.CellStart,
                sorted = store.Sorted.AsArray(),
            }.Schedule();

            var step = new WorkerStepJob
            {
                dt = dt,
                size = size,
                p = _params,
                cur = cur,
                next = next,
                tiers = _tiers,
                steering = w.Steering.Field,
                open = w.Pheromones.Open,
                homing = w.Pheromones.HomingDistance,
                temperature = w.Env.Temperature,
                alarm = field.GetSubArray((int)PheromoneChannel.Alarm * tiles, tiles),
                gather = field.GetSubArray((int)PheromoneChannel.Gather * tiles, tiles),
                nodes = w.Resources.Nodes.AsArray(),
                nodeInfo = w.Lookup.Info,
                nodeAt = w.Lookup.NodeAt,
                cellStart = store.CellStart,
                sorted = store.Sorted.AsArray(),
                cellsPerSide = store.CellsPerSide,
                cellSize = AgentStore.CellSize,
            }.Schedule(cur.Length, 256, hash);

            var resolve = new ResolveJob
            {
                size = size,
                trailOffset = (int)PheromoneChannel.Trail * tiles,
                trailDeposit = w.Balance.trailDeposit,
                agents = next,
                nodes = w.Resources.Nodes.AsArray(),
                nodeInfo = w.Lookup.Info,
                tiers = _tiers,
                source = w.Pheromones.Source,
                stock = w.Colony.Stock,
                capacity = w.Colony.Capacity,
                delivered = w.Colony.DeliveredThisSecond,
                gatherWeights = w.Colony.GatherWeights,
                dirtyChunks = _dirtyChunks,
            }.Schedule(step);

            resolve.Complete();
            store.Swap();

            for (int c = 0; c < _dirtyChunks.Length; c++)
            {
                if (_dirtyChunks[c] == 0) continue;
                _dirtyChunks[c] = 0;
                w.Resources.MarkChunkDirty(c);
            }
        }

        public void Dispose()
        {
            if (_tiers.IsCreated) _tiers.Dispose();
            if (_dirtyChunks.IsCreated) _dirtyChunks.Dispose();
        }
    }
}
