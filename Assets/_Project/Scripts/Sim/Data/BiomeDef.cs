using System.Collections.Generic;
using UnityEngine;

namespace Swarm.Data
{
    /// <summary>生物群系（GDD 2.3 / 2.4）。中心点按区域定义，见 RegionDef.biomes。</summary>
    [CreateAssetMenu(menuName = "Swarm/Biome", fileName = "Biome")]
    public sealed class BiomeDef : ScriptableObject
    {
        public string id;
        public string displayName;
        [Tooltip("地表顶点色（灰盒阶段代替群系贴图）")]
        public Color groundColor = Color.gray;
    }
}
