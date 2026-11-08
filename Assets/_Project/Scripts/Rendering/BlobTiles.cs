using System.Collections.Generic;
using UnityEngine;

namespace Swarm.Rendering
{
    /// <summary>
    /// 8 邻接 47 格连续瓦片（blob tileset）规则（GDD 2.7）。
    /// 角只在两条相邻边都连通时才算连通，256 种掩码化简后恰好 47 种。
    /// 图集由代码生成：每格中央方块 + 连通方向的边条 + 连通的角块。
    /// </summary>
    public static class BlobTiles
    {
        public const int N = 1, NE = 2, E = 4, SE = 8, S = 16, SW = 32, W = 64, NW = 128;
        public const int Columns = 8;
        public const int Rows = 6;

        static readonly byte[] s_maskToIndex = new byte[256];
        static readonly int[] s_indexToMask;

        public static int Count => s_indexToMask.Length;

        static BlobTiles()
        {
            var reduced = new SortedSet<int>();
            for (int m = 0; m < 256; m++) reduced.Add(Reduce(m));
            s_indexToMask = new int[reduced.Count];
            reduced.CopyTo(s_indexToMask);
            var lookup = new Dictionary<int, int>();
            for (int i = 0; i < s_indexToMask.Length; i++) lookup[s_indexToMask[i]] = i;
            for (int m = 0; m < 256; m++) s_maskToIndex[m] = (byte)lookup[Reduce(m)];
        }

        public static int Reduce(int m)
        {
            if ((m & N) == 0 || (m & E) == 0) m &= ~NE;
            if ((m & S) == 0 || (m & E) == 0) m &= ~SE;
            if ((m & S) == 0 || (m & W) == 0) m &= ~SW;
            if ((m & N) == 0 || (m & W) == 0) m &= ~NW;
            return m;
        }

        public static int IndexOf(int mask) => s_maskToIndex[mask & 0xFF];
        public static int MaskOf(int index) => s_indexToMask[index];

        /// <summary>图集中第 index 格的 UV 矩形（xMin, yMin, width, height）。</summary>
        public static Rect UvRect(int index)
        {
            int cx = index % Columns, cy = index / Columns;
            const float w = 1f / Columns, h = 1f / Rows;
            return new Rect(cx * w, cy * h, w, h);
        }

        public static Texture2D BuildAtlas(int tilePixels = 32)
        {
            int width = Columns * tilePixels, height = Rows * tilePixels;
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, true)
            {
                name = "BlobAtlas",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            var px = new Color32[width * height];
            const float inset = 0.24f;
            const int ss = 3; // 超采样抗锯齿
            for (int index = 0; index < Count; index++)
            {
                int mask = s_indexToMask[index];
                int ox = (index % Columns) * tilePixels, oy = (index / Columns) * tilePixels;
                for (int y = 0; y < tilePixels; y++)
                for (int x = 0; x < tilePixels; x++)
                {
                    int hit = 0;
                    for (int sy = 0; sy < ss; sy++)
                    for (int sx = 0; sx < ss; sx++)
                    {
                        float u = (x + (sx + 0.5f) / ss) / tilePixels;
                        float v = (y + (sy + 0.5f) / ss) / tilePixels;
                        if (Inside(mask, u, v, inset)) hit++;
                    }
                    float a = hit / (float)(ss * ss);
                    // 轻微斑点，让地被看起来不像纯色块
                    uint hash = (uint)((ox + x) * 73856093 ^ (oy + y) * 19349663);
                    float speckle = 0.82f + 0.18f * ((hash % 1000) / 1000f);
                    byte alpha = (byte)Mathf.RoundToInt(a * speckle * 255f);
                    px[(oy + y) * width + ox + x] = new Color32(255, 255, 255, alpha);
                }
            }
            tex.SetPixels32(px);
            tex.Apply(true, false);
            return tex;
        }

        static bool Inside(int mask, float u, float v, float r)
        {
            bool inU = u >= r && u <= 1f - r;
            bool inV = v >= r && v <= 1f - r;
            if (inU && inV) return RoundedCenter(mask, u, v, r);
            if (inU && v > 1f - r) return (mask & N) != 0;
            if (inU && v < r) return (mask & S) != 0;
            if (inV && u > 1f - r) return (mask & E) != 0;
            if (inV && u < r) return (mask & W) != 0;
            if (u > 1f - r && v > 1f - r) return (mask & NE) != 0;
            if (u > 1f - r && v < r) return (mask & SE) != 0;
            if (u < r && v < r) return (mask & SW) != 0;
            return (mask & NW) != 0;
        }

        /// <summary>中央方块在“两侧都不连通”的角上做圆角。</summary>
        static bool RoundedCenter(int mask, float u, float v, float r)
        {
            const float radius = 0.16f;
            float lo = r + radius, hi = 1f - r - radius;
            float cu = Mathf.Clamp(u, lo, hi), cv = Mathf.Clamp(v, lo, hi);
            bool right = u > hi, left = u < lo, top = v > hi, bottom = v < lo;
            bool round =
                (right && top && (mask & (N | E)) == 0) ||
                (right && bottom && (mask & (S | E)) == 0) ||
                (left && bottom && (mask & (S | W)) == 0) ||
                (left && top && (mask & (N | W)) == 0);
            if (!round) return true;
            float du = u - cu, dv = v - cv;
            return du * du + dv * dv <= radius * radius;
        }
    }
}
