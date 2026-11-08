using NUnit.Framework;
using Swarm.Agents;
using Swarm.Core;
using Swarm.Data;
using Swarm.Fields;
using Unity.Mathematics;
using UnityEngine;

namespace Swarm.Tests
{
    public class AgentTests
    {
        static void Run(SimulationWorld w, double seconds)
        {
            var clock = new TickClock { MaxStepsPerFrame = int.MaxValue };
            int steps = clock.Advance(seconds);
            for (int i = 0; i < steps; i++) w.Step(clock.Step());
        }

        static ulong AgentHash(SimulationWorld w)
        {
            ulong h = 1469598103934665603UL;
            var a = w.Agents.Agents;
            for (int i = 0; i < a.Length; i++)
            {
                h = (h ^ math.asuint(a[i].Position.x)) * 1099511628211UL;
                h = (h ^ math.asuint(a[i].Position.y)) * 1099511628211UL;
                h = (h ^ a[i].State) * 1099511628211UL;
            }
            return h;
        }

        [Test]
        public void StartingWorkers_SpawnAtNest()
        {
            var content = SimContent.Defaults();
            using var w = SimulationWorld.Create(content, 0, 21);
            Assert.AreEqual(content.Balance.startingWorkers, w.Agents.Count);
        }

        [Test]
        public void Workers_GatherAndDeliver_AndLeaveTrails()
        {
            using var w = SimulationWorld.Create(SimContent.Defaults(), 0, 21);
            Run(w, 240.0);
            float delivered = w.Colony.TotalDelivered(0) + w.Colony.TotalDelivered(1) + w.Colony.TotalDelivered(2);
            Debug.Log($"[Test] delivered P {w.Colony.TotalDelivered(0):0} M {w.Colony.TotalDelivered(1):0} B {w.Colony.TotalDelivered(2):0}, " +
                      $"trail {w.Pheromones.Total(PheromoneChannel.Trail):0}");
            Assert.Greater(delivered, 0f, "workers should bring resources home");
            Assert.Greater(w.Pheromones.Total(PheromoneChannel.Trail), 0f, "returning workers should leave trail pheromone");
        }

        [Test]
        public void Workers_StayOnWalkableTiles()
        {
            using var w = SimulationWorld.Create(SimContent.Defaults(), 1, 8);
            Run(w, 60.0);
            var a = w.Agents.Agents;
            int bad = 0;
            for (int i = 0; i < a.Length; i++)
            {
                int x = (int)a[i].Position.x, y = (int)a[i].Position.y;
                if (!w.Env.IsGroundWalkable(w.Env.Index(x, y))) bad++;
            }
            Assert.AreEqual(0, bad, "no worker may stand on water, mountains or giant footprints");
        }

        [Test]
        public void SameSeed_SameAgents()
        {
            var content = SimContent.Defaults();
            using var a = SimulationWorld.Create(content, 0, 77);
            using var b = SimulationWorld.Create(content, 0, 77);
            a.SpawnWorkers(2000);
            b.SpawnWorkers(2000);
            Run(a, 30.0);
            Run(b, 30.0);
            Assert.AreEqual(AgentHash(a), AgentHash(b), "parallel step + ordered resolve must be deterministic");
            Assert.AreEqual(a.Colony.Stock[0], b.Colony.Stock[0]);
        }

        [Test]
        public void Performance_20000Workers_UnderBudget()
        {
            using var w = SimulationWorld.Create(SimContent.Defaults(), 0, 3);
            w.SpawnWorkers(20000 - w.Agents.Count);
            Run(w, 2.0); // 预热：Burst 编译、缓冲分配
            const int steps = 30;
            double sum = 0, worst = 0;
            var clock = new TickClock { MaxStepsPerFrame = int.MaxValue };
            clock.Advance(steps * TickClock.Dt10);
            for (int i = 0; i < steps; i++)
            {
                w.Step(clock.Step());
                double ms = w.SystemMs["Agents"];
                sum += ms;
                worst = math.max(worst, ms);
            }
            double avg = sum / steps;
            Debug.Log($"[Test] {w.Agents.Count} workers: agent step avg {avg:0.00} ms, worst {worst:0.00} ms; " +
                      $"steering build {w.Steering.LastBuildMs:0.00} ms, lookup {w.Lookup.LastBuildMs:0.00} ms");
            Assert.Less(avg, 8.0, "GDD 14: one 10 Hz tick for 20000 bugs must stay under 8 ms");
        }

        [Test]
        public void SearchingWorkers_StayInsideCoverage()
        {
            using var w = SimulationWorld.Create(SimContent.Defaults(), 0, 21);
            w.SpawnWorkers(800);
            Run(w, 120.0);
            var a = w.Agents.Agents;
            int outside = 0;
            for (int i = 0; i < a.Length; i++)
                if (!w.Coverage.IsCovered((int)a[i].Position.x, (int)a[i].Position.y)) outside++;
            Debug.Log($"[Test] {outside}/{a.Length} workers outside coverage after 120 s");
            Assert.Less(outside, a.Length * 0.02f, "workers should not wander out of the colony's coverage");
        }

        [Test]
        public void PaintedGather_AttractsWorkersFromAfar()
        {
            using var w = SimulationWorld.Create(SimContent.Defaults(), 0, 21);
            Assert.IsTrue(w.Buildings.TryGetNestCore(out var core));
            // 找一块离巢心 14–20 格、在覆盖内、可从巢口到达的地面
            int2 spot = default;
            bool found = false;
            for (int r = 14; r <= 20 && !found; r++)
            for (int k = 0; k < 64 && !found; k++)
            {
                float ang = k / 64f * math.PI * 2f;
                int2 t = core.Tile + new int2((int)(math.cos(ang) * r), (int)(math.sin(ang) * r));
                if (!w.Env.InBounds(t) || !w.Coverage.IsCovered(t.x, t.y)) continue;
                if (w.Pheromones.HomingDistance[w.Env.Index(t)] == PheromoneFields.Unreachable) continue;
                spot = t;
                found = true;
            }
            Assert.IsTrue(found, "need a reachable covered spot");
            var c = new float2(spot) + 0.5f;
            Assert.Greater(w.PaintPheromone(PheromoneChannel.Gather, c, 4f, 1f), 0);

            w.SpawnWorkers(400); // 工虫远多于采集区容量，才能看出饱和
            Run(w, 1.0);
            int tile = w.Env.Index(spot);
            Assert.IsTrue(w.GatherRegions.TryGetRegionAt(tile, out var region), "painted spot should form a gather area");
            float capacity = region.Capacity;

            int peak = 0, settled = 0;
            for (int s = 0; s < 18; s++)
            {
                Run(w, 5.0);
                w.GatherRegions.TryGetRegionAt(tile, out region);
                peak = math.max(peak, region.Crowd);
                if (s >= 12) settled = math.max(settled, region.Crowd);
            }
            Debug.Log($"[Test] gather area capacity {capacity:0}, peak crowd {peak}, settled crowd {settled}, workers {w.Agents.Count}");
            Assert.GreaterOrEqual(peak, math.min(capacity, 3f), "painting gather should pull workers to it");
            Assert.LessOrEqual(settled, capacity * 1.5f + 8f, "once full, the area should stop pulling more workers");
        }
    }
}
