using System;
using System.Collections.Generic;
using UnityEngine;

namespace Swarm.Data
{
    public enum ResourceKind : byte { Protein = 0, Mineral = 1, Build = 2 }

    public enum RegrowMode : byte
    {
        /// <summary>取空后按计时再生。</summary>
        Timer,
        /// <summary>每年秋季开始时刷新（落叶堆）。</summary>
        AutumnRefresh,
        /// <summary>不再生（矿物露头）。</summary>
        Never,
    }

    public enum Season : byte { Spring = 0, Summer = 1, Autumn = 2, Winter = 3 }

    [Flags]
    public enum SeasonMask : byte
    {
        None = 0,
        Spring = 1,
        Summer = 2,
        Autumn = 4,
        Winter = 8,
        All = Spring | Summer | Autumn | Winter,
    }

    /// <summary>灰盒外观。组合形状由多个基础网格拼成，见 ResourceRenderer。</summary>
    public enum NodeShape : byte
    {
        Sphere, Capsule, Cylinder, Cube,
        /// <summary>树干（副色）+ 两层树冠。</summary>
        Tree,
        /// <summary>菌柄（副色）+ 扁平菌盖。</summary>
        Mushroom,
        /// <summary>三块倾斜的岩石。</summary>
        Rocks,
        /// <summary>一丛细长的茎（草、芦苇）。</summary>
        Tuft,
        /// <summary>针叶树：粗树干 + 根部 + 逐层收窄的树冠。</summary>
        Conifer,
    }

    /// <summary>信息素通道（GDD 4.1）。归巢是独立的距离场，不在此列。</summary>
    public enum PheromoneChannel : byte
    {
        Gather = 0,       // 采集（玩家笔刷）
        Rally = 1,        // 集结 / 攻击（玩家笔刷）
        Avoid = 2,        // 回避（玩家笔刷）
        Trail = 3,        // 路径（返巢工虫留下）
        Alarm = 4,        // 警报（受伤 / 死亡）
        ScentProtein = 5, // 资源气味（环境）
        ScentMineral = 6,
        ScentBuild = 7,
        Danger = 8,       // 危险（毒性、捕食者，环境）
    }

    [Serializable]
    public struct PheromoneChannelSettings
    {
        public PheromoneChannel channel;
        [Tooltip("覆盖范围内每秒衰减比例")] public float decayInside;
        [Tooltip("覆盖范围外每秒衰减比例")] public float decayOutside;
        [Tooltip("扩散系数 D")] public float diffusion;
        [Tooltip("向目标层（玩家绘制层 / 环境重算值）靠拢的速度（每秒）；0 = 无目标层")]
        public float targetApproach;
        [Tooltip("浓度上限")] public float cap;
        [Tooltip("热力图显示时的满值")] public float displayScale;
        public Color color;
    }

    public static class SeasonUtil
    {
        public static SeasonMask ToMask(this Season s) => (SeasonMask)(1 << (int)s);
    }

    /// <summary>某群系对某物（资源实体、地被）的偏好权重。</summary>
    [Serializable]
    public struct BiomeWeight
    {
        public BiomeDef biome;
        public float weight;

        public BiomeWeight(BiomeDef biome, float weight)
        {
            this.biome = biome;
            this.weight = weight;
        }
    }

    /// <summary>区域内的一个群系及其在（温度，湿度）平面上的中心点（GDD 2.4）。</summary>
    [Serializable]
    public struct RegionBiome
    {
        public BiomeDef biome;
        [Tooltip("°C")] public float centerTemperature;
        [Range(0, 1)] public float centerHumidity;

        public RegionBiome(BiomeDef biome, float centerTemperature, float centerHumidity)
        {
            this.biome = biome;
            this.centerTemperature = centerTemperature;
            this.centerHumidity = centerHumidity;
        }
    }

    public static class BiomeWeightUtil
    {
        public static float WeightOf(List<BiomeWeight> list, BiomeDef biome)
        {
            if (list == null || biome == null) return 0f;
            float w = 0f;
            foreach (var bw in list)
                if (bw.biome == biome) w += bw.weight;
            return w;
        }
    }
}
