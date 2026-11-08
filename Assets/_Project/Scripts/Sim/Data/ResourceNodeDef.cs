using System.Collections.Generic;
using UnityEngine;

namespace Swarm.Data
{
    /// <summary>可采集资源实体（GDD 2.6）。</summary>
    [CreateAssetMenu(menuName = "Swarm/Resource Node", fileName = "ResourceNode")]
    public sealed class ResourceNodeDef : ScriptableObject
    {
        public string id;
        public string displayName;
        public ResourceKind kind;
        [Tooltip("储量；无限资源时无意义")]
        public float amount = 20f;

        [Header("Harvest")]
        [Tooltip("无限储量：永不取空，但采集很慢")]
        public bool infinite;
        [Tooltip("单只工虫的采集速度（资源 / 秒），M3 使用")]
        public float harvestRatePerSecond = 5f;

        [Header("Footprint")]
        [Tooltip("占地 N × N Tile。> 1 的巨型实体阻挡地面单位（不挡空中单位）")]
        [Range(1, 4)] public int footprint = 1;

        [Header("Regrowth")]
        public RegrowMode regrowMode = RegrowMode.Timer;
        public float regrowSeconds = 60f;
        public bool isPlant = true;
        [Tooltip("温度低于 coldStopTemperature 时停止再生")]
        public bool stopsInCold = true;
        [Tooltip("湿度低于该值时停止再生，0 = 不限")]
        [Range(0, 1)] public float minHumidity;
        public SeasonMask regrowSeasons = SeasonMask.All;

        [Header("Habitat (plants)")]
        [Tooltip("喜欢的土壤含水量范围（干旱图）")]
        [Range(0, 1)] public float moistureMin;
        [Range(0, 1)] public float moistureMax = 1f;
        [Tooltip("需要的最低营养土肥力")]
        [Range(0, 1)] public float fertilityMin;

        [Header("Spawning")]
        [Tooltip("在完全匹配的群系中，每 16 × 16 区块的期望簇数")]
        public float densityPerChunk = 2f;
        [Tooltip("每簇实体数量范围（巨型实体固定为 1）")]
        public int clusterMin = 1;
        public int clusterMax = 1;
        public List<BiomeWeight> biomes = new List<BiomeWeight>();
        [Tooltip("悬崖层额外权重")] public float cliffAffinity;
        [Tooltip("水边（2 Tile 内）额外权重")] public float waterEdgeAffinity;

        [Header("Visual (graybox)")]
        public NodeShape shape = NodeShape.Sphere;
        [Tooltip("整体尺寸（Tile）：x/z 宽，y 高")]
        public Vector3 size = new Vector3(0.5f, 0.5f, 0.5f);
        [Tooltip("个体尺寸随机范围")]
        public Vector2 sizeJitter = new Vector2(0.75f, 1.3f);
        public Color color = Color.white;
        [Tooltip("组合形状的第二种颜色（树干、菌柄）")]
        public Color secondaryColor = new Color(0.35f, 0.25f, 0.18f);
    }
}
