using System;
using Unity.Collections;
using Unity.Mathematics;

namespace Swarm.World
{
    [Flags]
    public enum TileFlags : byte
    {
        None = 0,
        Ramp = 1 << 0,
        Water = 1 << 1,
        /// <summary>硬性地图障碍（高山）：地面单位和空中单位都无法逾越。</summary>
        Obstacle = 1 << 2,
        /// <summary>巨型资源实体占地：阻挡地面单位，不挡空中单位。</summary>
        NodeBlock = 1 << 3,
    }

    /// <summary>
    /// 环境数据网格（GDD 2.2 / 14.3）：每个字段一个 NativeArray，索引 = y * Size + x。
    /// 另含 96 × 96 的地下单元网格（M4 使用）。
    /// </summary>
    public sealed class EnvGrid : IDisposable
    {
        public readonly int Size;
        public readonly int UndergroundSize;
        public int Length => Size * Size;

        /// <summary>不含季节的本地温度 = 区域基准 + 噪声偏移（°C）。</summary>
        public NativeArray<float> TemperatureBase;
        /// <summary>当前温度 = TemperatureBase + 季节偏移（°C），1 Hz 更新。</summary>
        public NativeArray<float> Temperature;
        public NativeArray<float> Humidity;
        public NativeArray<float> SoilMoisture;
        /// <summary>营养土 / 肥力 0..1：成片分布，决定喜肥植物在哪里成团。</summary>
        public NativeArray<float> Fertility;
        public NativeArray<float> Acidity;
        public NativeArray<float> Toxicity;
        public NativeArray<byte> CoverType;   // 0 = 无，1.. = GroundCoverDef 索引 + 1
        public NativeArray<float> CoverAmount;
        public NativeArray<float4> BiomeWeights; // 对应 RegionDef.biomes 的前 4 项
        public NativeArray<byte> HeightLevel; // 0 陆地，1 悬崖
        public NativeArray<byte> Flags;       // TileFlags
        public NativeArray<float> HeightNoise; // 生成用的高度噪声 0..1，保留供调试
        public NativeArray<byte> WaterDistance; // 到最近水面的 4 邻接距离，255 = 很远
        public NativeArray<byte> ObstacleDepth; // 高山内部深度：0 = 非山，1 = 山脚，越大越靠近山脊

        public NativeArray<byte> UgDug;
        public NativeArray<byte> UgChamberType;
        public NativeArray<float> UgToxicity;

        public EnvGrid(int size)
        {
            Size = size;
            UndergroundSize = size / 2;
            int n = size * size;
            int u = UndergroundSize * UndergroundSize;
            const Allocator a = Allocator.Persistent;
            TemperatureBase = new NativeArray<float>(n, a);
            Temperature = new NativeArray<float>(n, a);
            Humidity = new NativeArray<float>(n, a);
            SoilMoisture = new NativeArray<float>(n, a);
            Fertility = new NativeArray<float>(n, a);
            Acidity = new NativeArray<float>(n, a);
            Toxicity = new NativeArray<float>(n, a);
            CoverType = new NativeArray<byte>(n, a);
            CoverAmount = new NativeArray<float>(n, a);
            BiomeWeights = new NativeArray<float4>(n, a);
            HeightLevel = new NativeArray<byte>(n, a);
            Flags = new NativeArray<byte>(n, a);
            HeightNoise = new NativeArray<float>(n, a);
            WaterDistance = new NativeArray<byte>(n, a);
            ObstacleDepth = new NativeArray<byte>(n, a);
            UgDug = new NativeArray<byte>(u, a);
            UgChamberType = new NativeArray<byte>(u, a);
            UgToxicity = new NativeArray<float>(u, a);
        }

        public int Index(int x, int y) => y * Size + x;
        public int Index(int2 p) => p.y * Size + p.x;
        public int2 Coord(int i) => new int2(i % Size, i / Size);
        public bool InBounds(int x, int y) => (uint)x < (uint)Size && (uint)y < (uint)Size;
        public bool InBounds(int2 p) => InBounds(p.x, p.y);

        public bool IsCliff(int i) => HeightLevel[i] == 1;
        public bool IsWater(int i) => (Flags[i] & (byte)TileFlags.Water) != 0;
        public bool IsRamp(int i) => (Flags[i] & (byte)TileFlags.Ramp) != 0;
        public bool IsObstacle(int i) => (Flags[i] & (byte)TileFlags.Obstacle) != 0;
        public bool IsNodeBlocked(int i) => (Flags[i] & (byte)TileFlags.NodeBlock) != 0;

        /// <summary>地面单位能否站在该 Tile（悬崖可经坡道或攀爬到达，不算阻挡）。</summary>
        public bool IsGroundWalkable(int i) =>
            (Flags[i] & (byte)(TileFlags.Obstacle | TileFlags.Water | TileFlags.NodeBlock)) == 0;

        /// <summary>空中单位能否飞越该 Tile：只有高山阻挡。</summary>
        public bool IsAirPassable(int i) => !IsObstacle(i);

        public void Dispose()
        {
            if (!TemperatureBase.IsCreated) return;
            TemperatureBase.Dispose(); Temperature.Dispose(); Humidity.Dispose(); SoilMoisture.Dispose(); Fertility.Dispose();
            Acidity.Dispose(); Toxicity.Dispose(); CoverType.Dispose(); CoverAmount.Dispose();
            BiomeWeights.Dispose(); HeightLevel.Dispose(); Flags.Dispose(); HeightNoise.Dispose();
            WaterDistance.Dispose(); ObstacleDepth.Dispose(); UgDug.Dispose(); UgChamberType.Dispose(); UgToxicity.Dispose();
        }
    }
}
