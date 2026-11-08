using NUnit.Framework;
using Swarm.Data;
using Swarm.Rendering;
using Swarm.World;

namespace Swarm.Tests
{
    public class WorldGenTests
    {
        static ulong GenerateHash(DefaultContent.Set d, int region, uint seed, out int nodes, out int cliffs, out int water)
        {
            var b = d.balance;
            using var env = new EnvGrid(b.mapSize);
            WorldGen.Generate(env, d.regions[region], b, seed);
            using var cover = new GroundCoverField(env, d.regions[region], d.covers, b);
            cover.Initialize(env, seed, b);
            using var res = new ResourceField();
            res.Generate(env, d.regions[region], b, d.resources, seed);
            nodes = res.Nodes.Length;
            cliffs = 0;
            water = 0;
            for (int i = 0; i < env.Length; i++)
            {
                if (env.IsCliff(i)) cliffs++;
                if (env.IsWater(i)) water++;
            }
            return WorldHash.Compute(env, res);
        }

        [Test]
        public void SameSeed_SameMap()
        {
            var d = DefaultContent.Create();
            for (int r = 0; r < d.regions.Length; r++)
            {
                ulong a = GenerateHash(d, r, 777, out _, out _, out _);
                ulong b = GenerateHash(d, r, 777, out _, out _, out _);
                Assert.AreEqual(a, b, $"region {d.regions[r].id}");
            }
        }

        [Test]
        public void DifferentSeed_DifferentMap()
        {
            var d = DefaultContent.Create();
            Assert.AreNotEqual(GenerateHash(d, 0, 1, out _, out _, out _), GenerateHash(d, 0, 2, out _, out _, out _));
        }

        [Test]
        public void Regions_ProduceDifferentMaps_WithResourcesAndCliffs()
        {
            var d = DefaultContent.Create();
            var hashes = new ulong[d.regions.Length];
            for (int r = 0; r < d.regions.Length; r++)
            {
                hashes[r] = GenerateHash(d, r, 42, out int nodes, out int cliffs, out _);
                Assert.Greater(nodes, 200, $"{d.regions[r].id} should have resource nodes");
                Assert.Greater(cliffs, 0, $"{d.regions[r].id} should have cliffs");
            }
            Assert.AreNotEqual(hashes[0], hashes[1]);
            Assert.AreNotEqual(hashes[1], hashes[2]);
            Assert.AreNotEqual(hashes[0], hashes[2]);
        }

        [Test]
        public void Mountains_ExistAndNeverSplitTheMap()
        {
            var d = DefaultContent.Create();
            for (int r = 0; r < d.regions.Length; r++)
            for (uint seed = 1; seed <= 2; seed++)
            {
                var b = d.balance;
                using var env = new EnvGrid(b.mapSize);
                WorldGen.Generate(env, d.regions[r], b, seed);
                using var res = new ResourceField();
                res.Generate(env, d.regions[r], b, d.resources, seed);

                int obstacles = 0, walkable = 0, start = -1;
                for (int i = 0; i < env.Length; i++)
                {
                    if (env.IsObstacle(i)) obstacles++;
                    if (env.IsGroundWalkable(i)) { walkable++; if (start < 0) start = i; }
                }
                float frac = obstacles / (float)env.Length;
                Assert.Greater(frac, 0.03f, $"{d.regions[r].id}/{seed}: too few mountains ({frac:P1})");
                Assert.Less(frac, 0.45f, $"{d.regions[r].id}/{seed}: too many mountains ({frac:P1})");

                // 所有可走 Tile 必须连通（悬崖算可走）
                var seen = new bool[env.Length];
                var q = new System.Collections.Generic.Queue<int>();
                q.Enqueue(start); seen[start] = true;
                int reached = 0;
                while (q.Count > 0)
                {
                    int t = q.Dequeue(); reached++;
                    int x = t % env.Size, y = t / env.Size;
                    void Try(int nx, int ny)
                    {
                        if (!env.InBounds(nx, ny)) return;
                        int j = env.Index(nx, ny);
                        if (seen[j] || !env.IsGroundWalkable(j)) return;
                        seen[j] = true; q.Enqueue(j);
                    }
                    Try(x + 1, y); Try(x - 1, y); Try(x, y + 1); Try(x, y - 1);
                }
                // 巨型实体占地后可能留下极少量被包住的格子，允许 1% 误差
                Assert.GreaterOrEqual(reached, (int)(walkable * 0.99f), $"{d.regions[r].id}/{seed}: map split by mountains");
            }
        }

        [Test]
        public void GiantNodes_BlockGroundAndAreInfiniteWhenConfigured()
        {
            var d = DefaultContent.Create();
            var b = d.balance;
            int giants = 0;
            for (int r = 0; r < d.regions.Length; r++)
            {
                using var env = new EnvGrid(b.mapSize);
                WorldGen.Generate(env, d.regions[r], b, 9);
                using var res = new ResourceField();
                res.Generate(env, d.regions[r], b, d.resources, 9);
                for (int n = 0; n < res.Nodes.Length; n++)
                {
                    var node = res.Nodes[n];
                    if (node.Footprint <= 1) continue;
                    giants++;
                    int i = ResourceField.TileIndex(env, node.Position);
                    Assert.IsTrue(env.IsNodeBlocked(i));
                    Assert.IsFalse(env.IsGroundWalkable(i));
                    Assert.IsTrue(env.IsAirPassable(i));
                    Assert.AreEqual(d.resources[node.Def].infinite, node.Infinite != 0);
                }
                res.DepleteAll();
                for (int n = 0; n < res.Nodes.Length; n++)
                    if (res.Nodes[n].Infinite != 0) Assert.Greater(res.Nodes[n].Amount, 0f, "无限资源不会被取空");
            }
            Assert.Greater(giants, 0, "应至少生成一个巨型实体");
        }

        [Test]
        public void BiomeWeights_SumToOne()
        {
            var d = DefaultContent.Create();
            using var env = new EnvGrid(d.balance.mapSize);
            WorldGen.Generate(env, d.regions[0], d.balance, 5);
            for (int i = 0; i < env.Length; i += 97)
            {
                var w = env.BiomeWeights[i];
                Assert.AreEqual(1f, w.x + w.y + w.z + w.w, 1e-4f);
            }
        }

        [Test]
        public void BlobTiles_Has47Variants()
        {
            Assert.AreEqual(47, BlobTiles.Count);
            Assert.AreEqual(BlobTiles.IndexOf(BlobTiles.N | BlobTiles.E), BlobTiles.IndexOf(BlobTiles.N | BlobTiles.E | BlobTiles.SW));
            Assert.AreNotEqual(BlobTiles.IndexOf(BlobTiles.N | BlobTiles.E), BlobTiles.IndexOf(BlobTiles.N | BlobTiles.E | BlobTiles.NE));
        }
    }

    public class SeasonTests
    {
        [Test]
        public void Seasons_FollowWinterFraction()
        {
            // 冻原：冬季 40%，其余三季各 20%
            const float year = 1200f, winter = 0.4f;
            Assert.AreEqual(Season.Spring, SeasonClock.SeasonAt(0f, year, winter));
            Assert.AreEqual(Season.Summer, SeasonClock.SeasonAt(300f, year, winter));
            Assert.AreEqual(Season.Autumn, SeasonClock.SeasonAt(600f, year, winter));
            Assert.AreEqual(Season.Winter, SeasonClock.SeasonAt(720f, year, winter));
            Assert.AreEqual(Season.Winter, SeasonClock.SeasonAt(1199f, year, winter));
            Assert.AreEqual(480f, SeasonClock.SeasonLength(Season.Winter, year, winter), 1e-3f);
        }

        [Test]
        public void TemperatureCurve_PeaksMidSummer_BottomsMidWinter()
        {
            const float year = 1200f, winter = 0.2f, amp = 10f;
            float a = year * (1f - winter) / 3f;
            Assert.AreEqual(amp, SeasonClock.OffsetAt(1.5f * a, year, winter, amp), 1e-3f);
            Assert.AreEqual(-amp, SeasonClock.OffsetAt(3f * a + year * winter * 0.5f, year, winter, amp), 1e-3f);
            // 连续：年末与年初接近
            Assert.AreEqual(SeasonClock.OffsetAt(0f, year, winter, amp), SeasonClock.OffsetAt(year - 0.01f, year, winter, amp), 0.01f);
        }

        static ResourceNodeDef Def(DefaultContent.Set d, string id)
        {
            foreach (var r in d.resources) if (r.id == id) return r;
            Assert.Fail(id);
            return null;
        }

        [Test]
        public void Regrowth_RespectsSeasonAndClimate()
        {
            var d = DefaultContent.Create();
            float cold = d.balance.coldStopTemperature;
            var fern = Def(d, "fern");
            var grass = Def(d, "grass_seed");
            var conifer = Def(d, "conifer_seed");
            var fungus = Def(d, "fungus_cluster");
            var outcrop = Def(d, "mineral_outcrop");
            var leaves = Def(d, "leaf_litter");

            float Rate(ResourceNodeDef def, Season s, float temp, float hum = 0.7f) =>
                ResourceRules.RegrowRate(def, s, temp, hum, 0.6f, 0.05f, 0f, cold);

            Assert.Greater(Rate(fern, Season.Summer, 18f), 0f);
            Assert.AreEqual(0f, Rate(fern, Season.Winter, 18f), "蕨丛冬季停止");
            Assert.AreEqual(0f, Rate(grass, Season.Autumn, 18f), "草籽丛仅春夏");
            Assert.Greater(Rate(grass, Season.Spring, 18f), 0f);
            Assert.AreEqual(0f, Rate(grass, Season.Spring, 2f), "低于 5 °C 多数植物停止再生");
            Assert.Greater(Rate(conifer, Season.Winter, -5f), 0f, "针叶落籽秋冬也再生");
            Assert.AreEqual(0f, Rate(fungus, Season.Summer, 18f, 0.4f), "菌簇湿度 < 0.5 停止");
            Assert.AreEqual(0f, Rate(outcrop, Season.Summer, 18f), "矿物露头不再生");
            Assert.AreEqual(0f, Rate(leaves, Season.Summer, 18f), "落叶堆只在秋季刷新");

            float clean = ResourceRules.RegrowRate(fern, Season.Summer, 18f, 0.7f, 0.6f, 0.05f, 0f, cold);
            float toxic = ResourceRules.RegrowRate(fern, Season.Summer, 18f, 0.7f, 0.6f, 0.05f, 0.5f, cold);
            Assert.Less(toxic, clean, "毒性降低再生速度");
        }
    }
}
