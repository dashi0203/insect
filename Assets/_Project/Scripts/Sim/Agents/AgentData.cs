using System;
using Unity.Mathematics;

namespace Swarm.Agents
{
    public enum AgentState : byte
    {
        WorkerSearch = 0,
        WorkerHarvest = 1,
        WorkerReturn = 2,
        WorkerFlee = 3,
        SoldierIdle = 4,
    }

    public enum Faction : byte { Player = 0, Wild = 1, Hostile = 2 }

    [Flags]
    public enum AgentFlags : byte
    {
        None = 0,
        Alive = 1 << 0,
        // 并行步写出的请求，由单线程结算步按 Agent 顺序处理（保证确定性）
        RequestHarvest = 1 << 1,
        RequestUnload = 1 << 2,
        RequestRechoose = 1 << 3,
        DepositTrail = 1 << 4,
        RequestMask = RequestHarvest | RequestUnload | RequestRechoose | DepositTrail,
    }

    /// <summary>
    /// 单位数据（GDD 14.3），紧凑的值类型，存在 NativeList 里连续排列；不为单位创建任何托管对象。
    /// 64 字节：一条缓存行放一只虫子。
    /// </summary>
    public struct AgentData
    {
        public float2 Position;      // 8
        public float2 PrevPosition;  // 8  渲染插值用
        public float2 Velocity;      // 8
        public float Health;         // 4
        public float StateTimer;     // 4
        public float Carry;          // 4
        public float Age;            // 4
        public float WanderAngle;    // 4
        public int Target;           // 4  目标资源实体索引，−1 = 无
        public int Genome;           // 4
        public uint Rng;             // 4  每只虫子独立的随机数状态
        public byte Faction;
        public byte Tier;            // SizeTier
        public byte State;           // AgentState
        public byte Flags;           // AgentFlags
        public byte ResourceKind;    // 所选资源（搜索时响应的气味）
        public byte CarryKind;
        public byte Layer;           // 0 地表，1 地下（M4）
        public byte Species;

        public bool IsAlive => (Flags & (byte)AgentFlags.Alive) != 0;
        public bool Has(AgentFlags f) => (Flags & (byte)f) != 0;
    }

    public static class AgentRandom
    {
        /// <summary>xorshift32：比 Unity.Mathematics.Random 更省状态，结果确定。</summary>
        public static uint Next(ref uint s)
        {
            s ^= s << 13;
            s ^= s >> 17;
            s ^= s << 5;
            return s;
        }

        public static float NextFloat(ref uint s) => (Next(ref s) >> 8) * (1f / 16777216f);

        public static uint Seed(uint worldSeed, int id)
        {
            uint h = math.hash(new uint2(worldSeed * 747796405u + 1u, (uint)id));
            return h == 0 ? 0x9E3779B9u : h;
        }
    }
}
