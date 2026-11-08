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
    }
}
