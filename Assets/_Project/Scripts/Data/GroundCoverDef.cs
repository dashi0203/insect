using System.Collections.Generic;
using UnityEngine;

namespace Swarm.Data
{
    /// <summary>地被（GDD 2.7）：不可采集的连续瓦片层。</summary>
    [CreateAssetMenu(menuName = "Swarm/Ground Cover", fileName = "GroundCover")]
    public sealed class GroundCoverDef : ScriptableObject
    {
        public string id;
        public string displayName;
        public Color color = Color.green;

        [Header("Climate suitability")]
        [Tooltip("°C")] public float minTemperature = 0f;
        [Tooltip("°C")] public float maxTemperature = 30f;
        [Range(0, 1)] public float minHumidity;
        [Range(0, 1)] public float maxHumidity = 1f;
        public List<BiomeWeight> biomes = new List<BiomeWeight>();
        public float cliffAffinity;
        [Tooltip("适宜度达到该值才算“气候适宜”")]
        public float suitabilityThreshold = 0.35f;
    }
}
