using NUnit.Framework;
using Swarm.Core;
using Swarm.Data;
using Swarm.World;
using Unity.Mathematics;

namespace Swarm.Tests
{
    public class SimulationWorldTests
    {
        static void Run(SimulationWorld w, double seconds)
        {
            var clock = new TickClock { MaxStepsPerFrame = int.MaxValue };
            int steps = clock.Advance(seconds);
            for (int i = 0; i < steps; i++) w.Step(clock.Step());
        }

        [Test]
        public void Create_SameSeed_SameHash_AndNestCorePlaced()
        {
            var content = SimContent.Defaults();
            using var a = SimulationWorld.Create(content, 0, 321);
            using var b = SimulationWorld.Create(content, 0, 321);
            Assert.AreEqual(a.Stats.MapHash, b.Stats.MapHash);
            Assert.IsTrue(a.Buildings.TryGetNestCore(out var core));
            Assert.IsTrue(a.Coverage.IsCovered(core.Tile.x, core.Tile.y), "nest core must be inside its own coverage");
        }

        [Test]
        public void Step_RunsSystemsAtTheirRates()
        {
            using var w = SimulationWorld.Create(SimContent.Defaults(), 0, 5);
            Run(w, 3.0);
            Assert.AreEqual(3.0, w.Time, 1e-6, "10 Hz base steps");
            Assert.AreEqual(3.0, w.Resources.Time, 1e-6, "1 Hz resource ticks");
            Assert.AreEqual(3.0, w.Season.Time, 1e-6, "1 Hz season ticks");
            foreach (var name in new[] { "Pheromones", "Season", "Resources", "GroundCover", "Scent" })
                Assert.IsTrue(w.SystemMs.ContainsKey(name), $"system {name} should have run");
        }

        [Test]
        public void SameSeed_SameSimulation_AfterSteps()
        {
            var content = SimContent.Defaults();
            using var a = SimulationWorld.Create(content, 1, 99);
            using var b = SimulationWorld.Create(content, 1, 99);
            a.DebugDepleteAllResources();
            b.DebugDepleteAllResources();
            Run(a, 30.0);
            Run(b, 30.0);
            Assert.AreEqual(a.Resources.CountRegrowing, b.Resources.CountRegrowing);
            Assert.AreEqual(a.Resources.CountFull, b.Resources.CountFull);
            Assert.AreEqual(a.Pheromones.Total(PheromoneChannel.ScentProtein), b.Pheromones.Total(PheromoneChannel.ScentProtein), 1e-3);
        }

        [Test]
        public void Paint_OnlyInsideCoverage()
        {
            using var w = SimulationWorld.Create(SimContent.Defaults(), 0, 7);
            Assert.IsTrue(w.Buildings.TryGetNestCore(out var core));
            var inside = new float2(core.Tile) + 0.5f;
            Assert.Greater(w.PaintPheromone(PheromoneChannel.Gather, inside, 3f, 1f), 0);

            // 地图角落离巢心至少 40 Tile，一定在覆盖外
            var outside = new float2(2.5f, 2.5f);
            Assert.IsFalse(w.Coverage.IsCovered(2, 2));
            Assert.AreEqual(0, w.PaintPheromone(PheromoneChannel.Gather, outside, 2f, 1f));
        }

        [Test]
        public void RegrowingPlant_RecordsRateForRendering()
        {
            using var w = SimulationWorld.Create(SimContent.Defaults(), 0, 11);
            w.DebugDepleteAllResources();
            Run(w, 2.0);
            bool found = false;
            for (int n = 0; n < w.Resources.Nodes.Length && !found; n++)
            {
                var node = w.Resources.Nodes[n];
                if (node.RegrowState == 1) found = node.RegrowRate > 0f && node.RegrowTimer > 0f;
            }
            Assert.IsTrue(found, "a regrowing node should carry a positive rate and timer");
        }

        [Test]
        public void ChunkVersion_ChangesOnlyWhereNodesChange()
        {
            using var w = SimulationWorld.Create(SimContent.Defaults(), 0, 13);
            var res = w.Resources;
            int before0 = res.ChunkVersion(0), before1 = res.ChunkVersion(1);
            res.MarkChunkDirty(1);
            Assert.AreEqual(before0, res.ChunkVersion(0));
            Assert.AreEqual(before1 + 1, res.ChunkVersion(1));
        }
    }
}
