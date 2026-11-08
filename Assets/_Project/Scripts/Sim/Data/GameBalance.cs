using UnityEngine;

namespace Swarm.Data
{
    /// <summary>所有全局数值（GDD 14.5）。括号内为 GDD 小节。</summary>
    [CreateAssetMenu(menuName = "Swarm/Game Balance", fileName = "GameBalance")]
    public sealed class GameBalance : ScriptableObject
    {
        [Tooltip("配置数据版本，编辑器据此给旧资产补上新字段的默认值")]
        public int dataVersion;

        [Header("Map (2.1, enlarged to 384 at designer's request)")]
        public int mapSize = 384;
        public int terrainChunkSize = 32;
        public int resourceChunkSize = 16;

        [Header("Noise & biomes (2.4)")]
        public float noiseFrequency = 0.02f;
        public int noiseOctaves = 4;
        [Tooltip("fBm 输出放大倍数，使偏移能达到 ±满幅")]
        public float noiseContrast = 1.6f;
        [Tooltip("°C")] public float temperatureOffset = 4f;
        public float humidityOffset = 0.2f;
        public float biomeSigma = 0.15f;
        [Tooltip("温度归一化区间 °C（群系距离计算用）")]
        public float temperatureNormMin = -10f;
        public float temperatureNormMax = 30f;

        [Header("Cliffs & ramps (2.4)")]
        public float cliffThreshold = 0.62f;
        public int minCliffRegionTiles = 12;
        public int rampWidthMin = 2;
        public int rampWidthMax = 3;
        [Tooltip("悬崖边缘每多少 Tile 生成一条坡道")]
        public int rampEdgeTilesPerRamp = 40;
        public int maxRampsPerCliff = 4;

        [Header("Mountains (hard obstacle: blocks ground AND air)")]
        public float mountainFrequency = 0.007f;
        [Tooltip("山脊噪声阈值，越高山越少")]
        public float mountainThreshold = 0.74f;
        [Tooltip("低频遮罩，决定哪些区域有山脉")]
        public float mountainMaskThreshold = -0.1f;
        [Tooltip("阈值判定后向外膨胀的格数：加粗山脉、连接相近的山段")]
        public int mountainDilate = 2;
        [Tooltip("地图边缘山带宽度（Tile）")]
        public float borderMountainWidth = 14f;
        [Tooltip("小于该面积的孤立山块被移除")]
        public int minMountainTiles = 80;
        [Tooltip("被山隔开的小块可走区域直接填成山；更大的区域开山口")]
        public int minPocketTiles = 300;
        public int passWidth = 4;
        [Tooltip("高山 = 放大的悬崖：整块抬到几层悬崖高（崖壁从地面直通到顶）")]
        public float mountainVisibleLevels = 3f;
        [Tooltip("靠外几格保留岩石色顶面，再往里过渡成黑影")]
        public int mountainRimTiles = 2;
        [Tooltip("山脚平台：低频噪声超过该值的外圈段降低一截形成平台（越大平台越少）")]
        public float mountainLedgeThreshold = 0.3f;
        [Tooltip("平台比山顶低多少（2 = 一层悬崖）")]
        public float mountainLedgeDrop = 2f;
        [Tooltip("侧壁岩层条带的厚度（蛋糕横截面）")]
        public float mountainStrataBand = 0.5f;
        [Tooltip("鼠标悬停时高山虚化的半径（Tile）")]
        public float mountainFadeRadius = 12f;

        [Header("Soil: moisture (drought map) & fertility")]
        [Tooltip("土壤含水量 = 湿度 × 该权重 + 噪声 × 幅度 + 近水加成")]
        public float soilHumidityWeight = 0.6f;
        public float soilNoiseAmplitude = 0.4f;
        [Tooltip("干湿斑块的噪声频率（越高斑块越小）")]
        public float soilNoiseFrequency = 0.04f;
        [Tooltip("营养土斑块的噪声频率")]
        public float fertilityNoiseFrequency = 0.035f;
        [Tooltip("营养土对比度：越大肥沃区与贫瘠区界线越清楚")]
        public float fertilityContrast = 1.6f;
        [Tooltip("超级营养土热点：每 96 × 96 Tile 的个数（肥力 > 1，即 > 100）")]
        public float superFertilePer96 = 0.8f;
        public float superFertileRadiusMin = 6f;
        public float superFertileRadiusMax = 12f;
        [Tooltip("热点中心的肥力（1.5 = 150）")]
        public float superFertilePeak = 1.5f;

        [Header("Water")]
        public float waterThreshold = 0.3f;
        public float waterMinHumidity = 0.6f;
        public int minWaterRegionTiles = 6;

        [Header("Seasons (2.5)")]
        public float yearLength = 1200f;
        [Tooltip("°C")] public float seasonAmplitude = 10f;
        public float coldStopTemperature = 5f;
        public float freezeTemperature = 0f;
        public float coldMoveMultiplier = 0.7f;
        public float frostDamagePerSecond = 0.2f;

        [Header("Resource nodes (2.6)")]
        public float plantToxicDeathThreshold = 0.7f;
        public float plantToxicDeathSeconds = 30f;
        public float reseedToxicityThreshold = 0.2f;
        public float reseedIntervalSeconds = 60f;

        [Header("Ecological patches (resources gather into communities)")]
        [Tooltip("每 64 × 64 Tile 的生态斑块数")]
        public float ecoPatchesPer64 = 0.5f;
        public float ecoPatchRadiusMin = 5f;
        public float ecoPatchRadiusMax = 12f;
        [Tooltip("斑块中心落在水边（1–5 Tile）时的额外权重")]
        public float ecoPatchWaterBias = 3f;
        [Tooltip("斑块内的资源密度倍率")]
        public float ecoPatchDensity = 7f;
        [Tooltip("任何地带之外的零散资源密度倍率")]
        public float scatterDensity = 0.06f;

        [Header("Rule zones (resource communities)")]
        [Tooltip("水边植被带宽度（离水 Tile 数）")]
        public int waterBeltWidth = 4;
        [Tooltip("水边植被带强度（0..1，乘以 ecoPatchDensity）")]
        public float waterBeltStrength = 1f;
        [Tooltip("崖脚碎石带宽度：悬崖 / 高山脚下几格内多矿物")]
        public int cliffFootWidth = 2;
        public float cliffFootStrength = 0.7f;
        [Tooltip("植物生境（含水量 + 肥力都合适）的强度；普通区域密度还会再乘以肥力")]
        public float habitatStrength = 0.45f;
        [Tooltip("超级营养土中心的额外密度倍率（肥力达到峰值时 ×(1 + 该值)）")]
        public float superFertileDensityBoost = 2.5f;
        [Tooltip("巨型植物周围林下植物带的半径")]
        public float groveRadius = 7f;
        public float groveStrength = 0.9f;

        [Header("Ground cover (2.7)")]
        public float coverIntervalSeconds = 5f;
        public float coverGrow = 0.05f;
        public float coverShrink = 0.05f;
        public float coverToxicityFactor = 0.1f;
        public float coverDrawThreshold = 0.3f;

        [Header("Pheromones (4.1 / 4.2)")]
        public PheromoneChannelSettings[] pheromoneChannels = DefaultPheromoneChannels();
        [Tooltip("资源气味的传播距离（Tile，衰减到 1/e）")]
        public float scentRange = 14f;
        [Tooltip("危险场的传播距离")]
        public float dangerRange = 6f;
        [Tooltip("资源气味满值对应的储量（对数刻度）")]
        public float scentFullAmount = 150f;
        [Tooltip("无限资源的气味强度（> 1，比普通资源更醒目）")]
        public float scentInfiniteValue = 1.2f;
        [Tooltip("归巢场热力图的显示范围（Tile）")]
        public float homingDisplayRange = 90f;

        [Header("Brush (4.3)")]
        public float brushRadiusMin = 1f;
        public float brushRadiusMax = 12f;
        public float brushStrengthMin = 0.1f;
        public float brushStrengthMax = 1f;

        [Header("Coverage (4.4 / 10)")]
        public float nestCoreCoverageRadius = 24f;
        public float pheromoneNodeCoverageRadius = 12f;
        public float outpostCoverageRadius = 16f;

        [Header("Units: size tiers (5.1 / 5.2)")]
        public SizeTierSettings[] sizeTiers = SizeTierSettings.Defaults();

        [Header("Steering (4.5)")]
        public SteeringWeights workerSearchWeights = SteeringWeights.WorkerSearch;
        public SteeringWeights workerReturnWeights = SteeringWeights.WorkerReturn;
        public SteeringWeights soldierIdleWeights = SteeringWeights.SoldierIdle;
        [Tooltip("梯度饱和 g / (|g| + k)")] public float gradientSaturation = 0.2f;
        [Tooltip("游荡噪声强度（与梯度合力同量级）")] public float wanderStrength = 0.45f;
        [Tooltip("游荡方向每秒最大偏转（弧度）")] public float wanderTurnRate = 2.5f;
        [Tooltip("分离力作用半径（Tile）")] public float separationRadius = 0.7f;
        public float separationStrength = 0.6f;
        [Tooltip("速度向期望方向靠拢的速率（每秒）")] public float steeringResponse = 8f;
        [Tooltip("特大个体对回避和危险的权重倍率")] public float hugeAvoidMultiplier = 0.5f;

        [Header("Workers (4.6 / 7.2)")]
        [Tooltip("资源实体在该距离内时开始采集（Tile）")] public float harvestRange = 1.5f;
        [Tooltip("普通实体一次采集耗时（秒）；慢速实体按 携带量 ÷ 采集速度 计时，取较大者")]
        public float harvestSeconds = 2f;
        [Tooltip("返巢工虫每个 10 Hz 步留下的路径信息素")] public float trailDeposit = 0.5f;
        [Tooltip("离巢口的归巢距离 ≤ 该值时卸货（Tile）")] public int unloadHomingDistance = 2;
        [Tooltip("搜索超过该时间仍未找到所选资源时重新选择（秒）")] public float searchGiveUpSeconds = 60f;
        [Tooltip("生命低于该比例且警报高于阈值时逃跑")] public float workerFleeHealth = 0.3f;
        public float workerFleeAlarm = 2f;

        [Header("Colony (7.1)")]
        [Tooltip("巢心自带的每种资源存储")] public float nestCoreStorage = 30f;
        [Tooltip("每个储藏室单元为三种资源各 + 该值")] public float storageChamberCapacity = 60f;
        [Tooltip("开局储藏室单元数（巢室 M4 之前用这个数算上限）")] public int startingStorageChambers = 2;
        [Tooltip("开局工虫数（育幼 M5 之前直接在巢心生成）")] public int startingWorkers = 120;
        [Tooltip("开局工虫中中型的比例")] [Range(0, 1)] public float startingMediumFraction = 0.35f;

        [Header("Terrain mesh (14.4)")]
        public float cliffHeight = 2f;
        public float waterDepth = 0.25f;

        /// <summary>GDD 4.1 的通道表。</summary>
        public static PheromoneChannelSettings[] DefaultPheromoneChannels()
        {
            PheromoneChannelSettings S(PheromoneChannel c, float kIn, float kOut, float d, float approach, float cap,
                float scale, Color color) => new PheromoneChannelSettings
            {
                channel = c, decayInside = kIn, decayOutside = kOut, diffusion = d,
                targetApproach = approach, cap = cap, displayScale = scale, color = color,
            };
            return new[]
            {
                S(PheromoneChannel.Gather, 0f, 0.10f, 0.10f, 2f, 1f, 1f, new Color(0.25f, 0.9f, 0.3f)),
                S(PheromoneChannel.Rally, 0f, 0.10f, 0.10f, 2f, 1f, 1f, new Color(0.95f, 0.22f, 0.18f)),
                S(PheromoneChannel.Avoid, 0f, 0.10f, 0.10f, 2f, 1f, 1f, new Color(0.65f, 0.3f, 0.95f)),
                S(PheromoneChannel.Trail, 0.025f, 0.025f, 0.02f, 0f, 10f, 10f, new Color(1f, 0.9f, 0.2f)),
                S(PheromoneChannel.Alarm, 0.40f, 0.40f, 0.25f, 0f, 20f, 5f, new Color(1f, 0.5f, 0.1f)),
                S(PheromoneChannel.ScentProtein, 0f, 0f, 0.15f, 1.5f, 2f, 1.2f, new Color(1f, 0.45f, 0.7f)),
                S(PheromoneChannel.ScentMineral, 0f, 0f, 0.15f, 1.5f, 2f, 1.2f, new Color(0.3f, 0.85f, 1f)),
                S(PheromoneChannel.ScentBuild, 0f, 0f, 0.15f, 1.5f, 2f, 1.2f, new Color(0.8f, 0.6f, 0.35f)),
                S(PheromoneChannel.Danger, 0f, 0f, 0.15f, 1.5f, 1f, 1f, new Color(0.55f, 0.05f, 0.12f)),
            };
        }
    }
}
