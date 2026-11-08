using System.Collections.Generic;
using Swarm.Data;
using Unity.Mathematics;
using UnityEngine;

namespace Swarm.World
{
    /// <summary>群系权重相关的工具（GDD 2.4）。Tile 上的 float4 权重对应 RegionDef.biomes 的前 4 项。</summary>
    public static class Biomes
    {
        public const int MaxPerRegion = 4;

        public static int Count(RegionDef region) => math.min(region.biomes.Count, MaxPerRegion);

        /// <summary>把“群系 → 权重”表投影到某区域的 4 个群系槽上，Tile 上用 dot(weights, affinity) 求偏好。</summary>
        public static float4 Affinity(RegionDef region, List<BiomeWeight> list)
        {
            float4 a = 0;
            int n = Count(region);
            for (int i = 0; i < n; i++)
                a[i] = BiomeWeightUtil.WeightOf(list, region.biomes[i].biome);
            return a;
        }

        public static Color BlendColor(RegionDef region, float4 w)
        {
            Color c = Color.black;
            int n = Count(region);
            for (int i = 0; i < n; i++)
                c += region.biomes[i].biome.groundColor * w[i];
            c.a = 1f;
            return c;
        }

        /// <summary>
        /// Tile 对各群系的权重 = exp(−d² / σ²) 归一化（d 在归一化的温度–湿度空间中）。
        /// 所有权重都极小时退化为最近群系的 one-hot，保证总和为 1。
        /// </summary>
        public static float4 Weights(float tempNorm, float humidity, float4 centerT, float4 centerH, int count, float sigma)
        {
            float4 dx = centerT - tempNorm;
            float4 dy = centerH - humidity;
            float4 d2 = dx * dx + dy * dy;
            bool4 valid = new bool4(count > 0, count > 1, count > 2, count > 3);
            float4 w = math.select(0f, math.exp(-d2 / (sigma * sigma)), valid);
            float sum = math.csum(w);
            if (sum > 1e-6f) return w / sum;

            float4 masked = math.select(float.MaxValue, d2, valid);
            float best = math.cmin(masked);
            float4 onehot = math.select(0f, 1f, masked == best);
            return onehot / math.max(1f, math.csum(onehot));
        }
    }
}
