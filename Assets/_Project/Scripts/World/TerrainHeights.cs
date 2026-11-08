using Swarm.Data;
using Unity.Mathematics;

namespace Swarm.World
{
    /// <summary>
    /// 地表几何高度（渲染用）。陆地 0、悬崖 cliffHeight、水面 −waterDepth。
    /// 坡道 Tile 的角点：接触非坡道陆地 → 0；否则接触非坡道悬崖 → 悬崖高；只接触坡道 → 半高。
    /// 高山 Tile：放大的悬崖——整块抬到 mountainVisibleLevels 层悬崖高，崖壁从地面直通到顶；
    /// 外圈少量连续段降一层形成山脚平台；顶面靠外 mountainRimTiles 格是岩石，再往里是黑影。
    /// </summary>
    public static class TerrainHeights
    {
        public static float TileBase(EnvGrid env, GameBalance b, int i) =>
            env.IsWater(i) ? -b.waterDepth : env.HeightLevel[i] * b.cliffHeight;

        /// <summary>Tile 四个角的高度：x = SW, y = SE, z = NE, w = NW。</summary>
        public static float4 Corners(EnvGrid env, GameBalance b, int x, int y)
        {
            int i = env.Index(x, y);
            if (env.IsObstacle(i)) return MountainTileHeight(env, b, x, y);
            if (!env.IsRamp(i)) return TileBase(env, b, i);
            return new float4(
                RampCorner(env, b, x, y),
                RampCorner(env, b, x + 1, y),
                RampCorner(env, b, x + 1, y + 1),
                RampCorner(env, b, x, y + 1));
        }

        static float RampCorner(EnvGrid env, GameBalance b, int cx, int cy)
        {
            bool low = false, high = false;
            for (int dy = -1; dy <= 0; dy++)
            for (int dx = -1; dx <= 0; dx++)
            {
                int tx = cx + dx, ty = cy + dy;
                if (!env.InBounds(tx, ty)) continue;
                int i = env.Index(tx, ty);
                if (env.IsRamp(i) || env.IsObstacle(i)) continue;
                if (env.HeightLevel[i] == 0) low = true;
                else high = true;
            }
            if (low) return 0f;
            if (high) return b.cliffHeight;
            return b.cliffHeight * 0.5f;
        }

        /// <summary>高山可见部分的最大高度 = mountainVisibleLevels 层悬崖。</summary>
        public static float MountainVisibleCap(GameBalance b) => b.mountainVisibleLevels * b.cliffHeight;

        /// <summary>高山 Tile 的几何高度（平顶）：真实高度夹到可见上限。</summary>
        public static float MountainTileHeight(EnvGrid env, GameBalance b, int x, int y) =>
            math.min(MountainTrueHeight(env, b, x, y), MountainVisibleCap(b));

        /// <summary>高山 Tile 的“真实”高度（不夹上限），超过可见上限的部分涂成黑影。</summary>
        public static float MountainTrueHeight(EnvGrid env, GameBalance b, int x, int y)
        {
            int i = env.Index(x, y);
            int depth = env.ObstacleDepth[i];
            float cap = MountainVisibleCap(b);

            // 最外圈：大部分直接到顶；少量连续段降一截，形成山脚平台
            if (depth <= 1)
            {
                float n = noise.cnoise(new float2(x, y) * 0.07f + 113.7f);
                return n > b.mountainLedgeThreshold ? math.max(b.cliffHeight, cap - b.mountainLedgeDrop) : cap;
            }
            // 往里：可见顶面保留 mountainRimTiles 格，再深的部分“高出”上限 → 黑影
            int over = depth - math.max(1, b.mountainRimTiles);
            return over <= 0 ? cap : cap + over * b.cliffHeight;
        }

        /// <summary>任意位置的地表高度（双线性插值所在 Tile 的四角）。</summary>
        public static float Sample(EnvGrid env, GameBalance b, float2 p)
        {
            int x = math.clamp((int)math.floor(p.x), 0, env.Size - 1);
            int y = math.clamp((int)math.floor(p.y), 0, env.Size - 1);
            float4 c = Corners(env, b, x, y);
            float fx = math.saturate(p.x - x), fy = math.saturate(p.y - y);
            return math.lerp(math.lerp(c.x, c.y, fx), math.lerp(c.w, c.z, fx), fy);
        }
    }
}
