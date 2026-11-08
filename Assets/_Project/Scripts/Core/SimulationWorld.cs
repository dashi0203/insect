using Unity.Mathematics;

namespace Swarm.Core
{
    /// <summary>
    /// 模拟世界的根数据。M0 只持有世界种子；后续里程碑往这里挂环境网格、信息素场、Agent 数据等。
    /// </summary>
    public sealed class SimulationWorld
    {
        public uint Seed { get; }

        public SimulationWorld(uint seed)
        {
            Seed = seed == 0 ? 1u : seed;
        }

        /// <summary>
        /// 为某个系统派生独立的随机数生成器（GDD 14.1）。
        /// 同一种子 + 同一 systemId 永远得到同一序列。
        /// </summary>
        public Random CreateRandom(uint systemId)
        {
            uint s = math.hash(new uint2(Seed, systemId));
            return new Random(s == 0 ? 0x9E3779B9u : s);
        }
    }

    /// <summary>各系统的随机数通道 id。新增系统时在这里加一项，不要改已有的值。</summary>
    public static class RandomStreams
    {
        public const uint WorldGen = 1;
        public const uint Ecology = 2;
        public const uint Agents = 3;
        public const uint Colony = 4;
        public const uint Humans = 5;
        public const uint Resources = 6;
        public const uint GroundCover = 7;
    }
}
