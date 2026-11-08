using System.Collections.Generic;
using UnityEngine;

namespace Swarm.Data
{
    /// <summary>区域 = 一组气候基准值 + 3–4 个群系（GDD 2.3）。</summary>
    [CreateAssetMenu(menuName = "Swarm/Region", fileName = "Region")]
    public sealed class RegionDef : ScriptableObject
    {
        public string id;
        public string displayName;
        [Tooltip("°C")] public float baseTemperature = 18f;
        [Range(0, 1)] public float baseHumidity = 0.75f;
        [Range(0, 1)] public float winterFraction = 0.2f;
        [Range(0, 1)] public float baseAcidity = 0.05f;
        [Tooltip("酸雨间隔（秒），0 = 无")] public float acidRainIntervalSeconds;
        [Tooltip("远方毒雾间隔（秒）")] public float distantFogIntervalSeconds = 480f;
        [Tooltip("最多 4 个")] public List<RegionBiome> biomes = new List<RegionBiome>();
    }
}
