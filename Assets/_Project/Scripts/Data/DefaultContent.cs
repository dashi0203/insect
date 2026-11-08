using System.Collections.Generic;
using UnityEngine;

namespace Swarm.Data
{
    /// <summary>
    /// GDD 中的默认数值，在内存里构造全部配置对象。
    /// 编辑器用它生成 Assets/_Project/Data 下的资产；测试和缺少资产时也用它。
    /// 修改数值请改资产，不要改这里（这里只决定“首次生成时”的值）。
    /// </summary>
    public static class DefaultContent
    {
        /// <summary>v1 = M1；v2 = 巨型 / 无限资源、成簇、组合外观、高山；v3 = 384 地图、大山脉、生态斑块、巨型比例；v4 = 规则地带、零散资源大减、簇更大；v5 = 肥力 / 干旱图驱动的植物生境；v6 = 超级营养土、植物密度按肥力缩放。</summary>
        public const int DataVersion = 6;

        public sealed class Set
        {
            public GameBalance balance;
            public BiomeDef[] biomes;
            public RegionDef[] regions;
            public ResourceNodeDef[] resources;
            public GroundCoverDef[] covers;
        }

        static T Make<T>(string id) where T : ScriptableObject
        {
            var o = ScriptableObject.CreateInstance<T>();
            o.name = id;
            return o;
        }

        static BiomeDef Biome(string id, string name, Color c)
        {
            var b = Make<BiomeDef>(id);
            b.id = id;
            b.displayName = name;
            b.groundColor = c;
            return b;
        }

        public static Set Create()
        {
            var s = new Set { balance = Make<GameBalance>("GameBalance") };
            s.balance.dataVersion = DataVersion;

            // ---- 群系（2.3）----
            var humus = Biome("humus_woodland", "腐殖林地", new Color(0.26f, 0.31f, 0.16f));
            var fungal = Biome("fungal_swamp", "菌类沼泽", new Color(0.33f, 0.27f, 0.31f));
            var shrub = Biome("shrub_grassland", "灌木草地", new Color(0.48f, 0.52f, 0.24f));
            var rocky = Biome("rocky_highland", "岩石高地", new Color(0.45f, 0.44f, 0.42f));
            var reed = Biome("reed_marsh", "苇荡浅沼", new Color(0.32f, 0.42f, 0.30f));
            var riparian = Biome("riparian_forest", "河岸林", new Color(0.19f, 0.37f, 0.20f));
            var tundra = Biome("tundra", "苔原", new Color(0.56f, 0.58f, 0.50f));
            var conifer = Biome("conifer_edge", "针叶林缘", new Color(0.18f, 0.28f, 0.23f));
            s.biomes = new[] { humus, fungal, shrub, rocky, reed, riparian, tundra, conifer };

            // ---- 区域（2.3）：群系中心点围绕区域基准值布置 ----
            var forest = Make<RegionDef>("humus_forest");
            forest.id = "humus_forest"; forest.displayName = "腐殖森林";
            forest.baseTemperature = 18f; forest.baseHumidity = 0.75f; forest.winterFraction = 0.20f;
            forest.baseAcidity = 0.05f; forest.acidRainIntervalSeconds = 0f; forest.distantFogIntervalSeconds = 8 * 60;
            forest.biomes = new List<RegionBiome>
            {
                new RegionBiome(humus, 17f, 0.79f),
                new RegionBiome(fungal, 20f, 0.92f),
                new RegionBiome(shrub, 21f, 0.58f),
                new RegionBiome(rocky, 14f, 0.62f),
            };

            var wetland = Make<RegionDef>("acid_wetland");
            wetland.id = "acid_wetland"; wetland.displayName = "酸雨湿地";
            wetland.baseTemperature = 20f; wetland.baseHumidity = 0.9f; wetland.winterFraction = 0.15f;
            wetland.baseAcidity = 0.2f; wetland.acidRainIntervalSeconds = 6 * 60; wetland.distantFogIntervalSeconds = 12 * 60;
            wetland.biomes = new List<RegionBiome>
            {
                new RegionBiome(reed, 21f, 0.98f),
                new RegionBiome(fungal, 19f, 0.82f),
                new RegionBiome(riparian, 17f, 0.90f),
            };

            var north = Make<RegionDef>("northern_tundra");
            north.id = "northern_tundra"; north.displayName = "北境冻原";
            north.baseTemperature = 4f; north.baseHumidity = 0.45f; north.winterFraction = 0.40f;
            north.baseAcidity = 0.03f; north.acidRainIntervalSeconds = 0f; north.distantFogIntervalSeconds = 16 * 60;
            north.biomes = new List<RegionBiome>
            {
                new RegionBiome(tundra, 2f, 0.40f),
                new RegionBiome(conifer, 7f, 0.55f),
                new RegionBiome(rocky, 5f, 0.30f),
            };
            s.regions = new[] { forest, wetland, north };

            // ---- 资源实体（2.6）----
            ResourceNodeDef Node(string id, string name, ResourceKind kind, float amount, RegrowMode mode, float regrow,
                float density, NodeShape shape, Vector3 size, Color color, params BiomeWeight[] biomes)
            {
                var n = Make<ResourceNodeDef>(id);
                n.id = id; n.displayName = name; n.kind = kind; n.amount = amount;
                n.regrowMode = mode; n.regrowSeconds = regrow; n.densityPerChunk = density;
                n.shape = shape; n.size = size; n.color = color;
                n.biomes = new List<BiomeWeight>(biomes);
                return n;
            }

            var fungusCluster = Node("fungus_cluster", "菌簇", ResourceKind.Protein, 25, RegrowMode.Timer, 60, 1.5f,
                NodeShape.Mushroom, new Vector3(0.45f, 0.4f, 0.45f), new Color(0.85f, 0.72f, 0.55f),
                new BiomeWeight(fungal, 1f), new BiomeWeight(humus, 0.6f));
            fungusCluster.minHumidity = 0.5f;
            fungusCluster.secondaryColor = new Color(0.9f, 0.88f, 0.8f);
            fungusCluster.clusterMin = 3; fungusCluster.clusterMax = 6;

            var grassSeed = Node("grass_seed", "草籽丛", ResourceKind.Protein, 15, RegrowMode.Timer, 90, 2.2f,
                NodeShape.Tuft, new Vector3(0.45f, 0.45f, 0.45f), new Color(0.82f, 0.78f, 0.35f),
                new BiomeWeight(shrub, 1f), new BiomeWeight(tundra, 0.8f));
            grassSeed.regrowSeasons = SeasonMask.Spring | SeasonMask.Summer;
            grassSeed.clusterMin = 3; grassSeed.clusterMax = 7;

            var coniferSeed = Node("conifer_seed", "针叶落籽", ResourceKind.Protein, 20, RegrowMode.Timer, 180, 2f,
                NodeShape.Sphere, new Vector3(0.3f, 0.25f, 0.3f), new Color(0.55f, 0.35f, 0.2f),
                new BiomeWeight(conifer, 1f));
            coniferSeed.stopsInCold = false; // 秋冬也再生
            coniferSeed.clusterMin = 2; coniferSeed.clusterMax = 4;

            var fern = Node("fern", "蕨丛", ResourceKind.Build, 40, RegrowMode.Timer, 120, 1.6f,
                NodeShape.Tuft, new Vector3(0.7f, 0.6f, 0.7f), new Color(0.25f, 0.58f, 0.25f),
                new BiomeWeight(humus, 1f), new BiomeWeight(riparian, 1f));
            fern.regrowSeasons = SeasonMask.Spring | SeasonMask.Summer | SeasonMask.Autumn;
            fern.clusterMin = 2; fern.clusterMax = 5;

            var reeds = Node("reed", "芦苇", ResourceKind.Build, 50, RegrowMode.Timer, 150, 1.5f,
                NodeShape.Tuft, new Vector3(0.4f, 1.1f, 0.4f), new Color(0.7f, 0.68f, 0.4f),
                new BiomeWeight(reed, 1f));
            reeds.regrowSeasons = SeasonMask.Spring | SeasonMask.Summer | SeasonMask.Autumn;
            reeds.waterEdgeAffinity = 0.6f;
            reeds.clusterMin = 4; reeds.clusterMax = 8;

            var leaves = Node("leaf_litter", "落叶堆", ResourceKind.Build, 20, RegrowMode.AutumnRefresh, 0, 2f,
                NodeShape.Cube, new Vector3(0.7f, 0.12f, 0.7f), new Color(0.6f, 0.4f, 0.2f),
                new BiomeWeight(humus, 1f), new BiomeWeight(riparian, 1f), new BiomeWeight(conifer, 0.6f));

            var outcrop = Node("mineral_outcrop", "矿物露头", ResourceKind.Mineral, 120, RegrowMode.Never, 0, 1.2f,
                NodeShape.Rocks, new Vector3(0.8f, 0.7f, 0.8f), new Color(0.45f, 0.55f, 0.7f),
                new BiomeWeight(rocky, 1f));
            outcrop.isPlant = false;
            outcrop.cliffAffinity = 0.8f;
            outcrop.clusterMin = 2; outcrop.clusterMax = 4;

            var saltMud = Node("salt_mud", "盐渍泥", ResourceKind.Mineral, 40, RegrowMode.Timer, 300, 1f,
                NodeShape.Cylinder, new Vector3(0.9f, 0.06f, 0.9f), new Color(0.85f, 0.85f, 0.8f),
                new BiomeWeight(reed, 0.6f), new BiomeWeight(fungal, 0.3f), new BiomeWeight(riparian, 0.3f));
            saltMud.isPlant = false;
            saltMud.waterEdgeAffinity = 1f;

            // ---- 巨型实体：占多个 Tile，阻挡地面单位；多为无限储量、采集极慢 ----
            ResourceNodeDef Giant(ResourceNodeDef n, int footprint, bool infinite, float harvestRate)
            {
                n.footprint = footprint;
                n.infinite = infinite;
                n.harvestRatePerSecond = harvestRate;
                n.sizeJitter = new Vector2(0.85f, 1.15f);
                return n;
            }

            var treeFern = Giant(Node("giant_tree_fern", "巨型树蕨", ResourceKind.Build, 0, RegrowMode.Never, 0, 0.18f,
                NodeShape.Tree, new Vector3(8f, 14f, 8f), new Color(0.22f, 0.5f, 0.22f),
                new BiomeWeight(humus, 1f), new BiomeWeight(riparian, 0.8f)), 3, true, 0.4f);
            treeFern.secondaryColor = new Color(0.3f, 0.22f, 0.14f);

            var oldPine = Giant(Node("ancient_pine", "古松", ResourceKind.Build, 0, RegrowMode.Never, 0, 0.2f,
                NodeShape.Conifer, new Vector3(8f, 22f, 8f), new Color(0.13f, 0.26f, 0.2f),
                new BiomeWeight(conifer, 1f), new BiomeWeight(tundra, 0.3f)), 3, true, 0.35f);
            oldPine.secondaryColor = new Color(0.33f, 0.24f, 0.17f);

            var giantReed = Giant(Node("giant_reed_bed", "巨苇丛", ResourceKind.Build, 0, RegrowMode.Never, 0, 0.3f,
                NodeShape.Tuft, new Vector3(4f, 7f, 4f), new Color(0.62f, 0.62f, 0.36f),
                new BiomeWeight(reed, 1f)), 2, true, 0.5f);
            giantReed.waterEdgeAffinity = 0.5f;

            var giantFungus = Giant(Node("giant_fungus", "巨菌", ResourceKind.Protein, 400, RegrowMode.Timer, 600, 0.2f,
                NodeShape.Mushroom, new Vector3(5.6f, 6f, 5.6f), new Color(0.7f, 0.45f, 0.4f),
                new BiomeWeight(fungal, 1f)), 2, false, 1f);
            giantFungus.minHumidity = 0.5f;
            giantFungus.secondaryColor = new Color(0.88f, 0.85f, 0.75f);

            var mineralVein = Giant(Node("mineral_vein", "巨型矿脉", ResourceKind.Mineral, 600, RegrowMode.Never, 0, 0.15f,
                NodeShape.Rocks, new Vector3(3.2f, 2.6f, 3.2f), new Color(0.4f, 0.5f, 0.68f),
                new BiomeWeight(rocky, 1f), new BiomeWeight(tundra, 0.3f)), 2, false, 1.5f);
            mineralVein.isPlant = false;
            mineralVein.cliffAffinity = 0.6f;

            // ---- 植物生境：含水量范围 + 最低肥力 ----
            void Habitat(ResourceNodeDef n, float mMin, float mMax, float fMin)
            {
                n.moistureMin = mMin; n.moistureMax = mMax; n.fertilityMin = fMin;
            }
            Habitat(fungusCluster, 0.65f, 1f, 0.55f);
            Habitat(grassSeed, 0.12f, 0.58f, 0.25f);
            Habitat(coniferSeed, 0.25f, 0.75f, 0.35f);
            Habitat(fern, 0.52f, 1f, 0.5f);
            Habitat(reeds, 0.78f, 1f, 0.2f);
            Habitat(leaves, 0.4f, 0.9f, 0.55f);
            Habitat(treeFern, 0.58f, 1f, 0.6f);
            Habitat(oldPine, 0.28f, 0.8f, 0.45f);
            Habitat(giantReed, 0.8f, 1f, 0.2f);
            Habitat(giantFungus, 0.7f, 1f, 0.6f);

            s.resources = new[]
            {
                fungusCluster, grassSeed, coniferSeed, fern, reeds, leaves, outcrop, saltMud,
                treeFern, oldPine, giantReed, giantFungus, mineralVein,
            };

            // ---- 地被（2.7）----
            var moss = Make<GroundCoverDef>("moss");
            moss.id = "moss"; moss.displayName = "苔藓"; moss.color = new Color(0.36f, 0.58f, 0.2f);
            moss.minTemperature = 0f; moss.maxTemperature = 28f; moss.minHumidity = 0.55f; moss.maxHumidity = 1f;
            moss.biomes = new List<BiomeWeight>
            {
                new BiomeWeight(humus, 1f), new BiomeWeight(riparian, 1f),
                new BiomeWeight(conifer, 0.8f), new BiomeWeight(reed, 0.3f),
            };

            var lichen = Make<GroundCoverDef>("lichen");
            lichen.id = "lichen"; lichen.displayName = "地衣"; lichen.color = new Color(0.74f, 0.74f, 0.55f);
            lichen.minTemperature = -30f; lichen.maxTemperature = 16f; lichen.minHumidity = 0f; lichen.maxHumidity = 0.7f;
            lichen.biomes = new List<BiomeWeight>
            {
                new BiomeWeight(tundra, 1f), new BiomeWeight(rocky, 0.8f), new BiomeWeight(conifer, 0.4f),
            };
            lichen.cliffAffinity = 0.5f;

            var mat = Make<GroundCoverDef>("fungal_mat");
            mat.id = "fungal_mat"; mat.displayName = "菌毯"; mat.color = new Color(0.62f, 0.46f, 0.62f);
            mat.minTemperature = 5f; mat.maxTemperature = 32f; mat.minHumidity = 0.7f; mat.maxHumidity = 1f;
            mat.biomes = new List<BiomeWeight> { new BiomeWeight(fungal, 1f) };

            s.covers = new[] { moss, lichen, mat };
            return s;
        }
    }
}
