using System;
using Swarm.Data;
using Unity.Collections;
using Unity.Mathematics;

namespace Swarm.Colony
{
    /// <summary>
    /// 族群库存（GDD 7.1 / 7.4）：三种资源的库存与上限、近 60 秒净变化，
    /// 以及工虫选资源用的缺口权重（GDD 7.2）。数组是 NativeArray，结算 Job 直接读写。
    /// 索引 = ResourceKind（0 蛋白质，1 矿物质，2 建筑质）。
    /// </summary>
    public sealed class ColonyStore : IDisposable
    {
        public const int Kinds = 3;
        const int HistorySeconds = 60;

        public NativeArray<float> Stock;
        public NativeArray<float> Capacity;
        /// <summary>工虫选资源的概率权重 = 需求 ÷ (库存 + 1)，1 Hz 重算。</summary>
        public NativeArray<float> GatherWeights;
        /// <summary>本秒累计卸货量，1 Hz 时计入历史后清零。</summary>
        public NativeArray<float> DeliveredThisSecond;

        readonly float[,] _history = new float[Kinds, HistorySeconds];
        int _historyHead, _historyCount;

        public float TotalDelivered(int kind) => _totalDelivered[kind];
        readonly float[] _totalDelivered = new float[Kinds];

        public ColonyStore(GameBalance b)
        {
            const Allocator a = Allocator.Persistent;
            Stock = new NativeArray<float>(Kinds, a);
            Capacity = new NativeArray<float>(Kinds, a);
            GatherWeights = new NativeArray<float>(Kinds, a);
            DeliveredThisSecond = new NativeArray<float>(Kinds, a);
            float cap = b.nestCoreStorage + b.storageChamberCapacity * math.max(0, b.startingStorageChambers);
            for (int k = 0; k < Kinds; k++)
            {
                Capacity[k] = cap;
                GatherWeights[k] = 1f;
            }
        }

        /// <summary>近 60 秒净变化（目前只有卸货收入；M5 起扣除维护费与育幼支出）。</summary>
        public float NetChange60(int kind)
        {
            float sum = 0f;
            for (int s = 0; s < _historyCount; s++) sum += _history[kind, s];
            return sum;
        }

        /// <summary>
        /// 1 Hz：记录本秒收支，重算选资源权重。
        /// 需求（GDD 7.2）：蛋白质 = 未来 60 秒维护费 + 幼虫剩余需求；矿物质 = 幼虫剩余需求 + 待建建筑；建筑质 = 待建建筑与巢室。
        /// 幼虫与待建项要到 M4 / M5 才有，在那之前每种资源再加上“距上限的空缺”，保证工虫有活可干。
        /// </summary>
        public void Tick(float upkeepPerMinute, float3 pendingDemand)
        {
            for (int k = 0; k < Kinds; k++)
            {
                _history[k, _historyHead] = DeliveredThisSecond[k];
                _totalDelivered[k] += DeliveredThisSecond[k];
                DeliveredThisSecond[k] = 0f;
            }
            _historyHead = (_historyHead + 1) % HistorySeconds;
            _historyCount = math.min(_historyCount + 1, HistorySeconds);

            float3 demand = pendingDemand;
            demand.x += upkeepPerMinute; // 未来 60 秒维护费
            float sum = 0f;
            for (int k = 0; k < Kinds; k++)
            {
                float gap = math.max(0f, Capacity[k] - Stock[k]);
                float w = (demand[k] + gap) / (Stock[k] + 1f);
                GatherWeights[k] = w;
                sum += w;
            }
            if (sum <= 1e-6f)
                for (int k = 0; k < Kinds; k++) GatherWeights[k] = 1f; // 全满：均匀选择
        }

        public void Dispose()
        {
            if (Stock.IsCreated) Stock.Dispose();
            if (Capacity.IsCreated) Capacity.Dispose();
            if (GatherWeights.IsCreated) GatherWeights.Dispose();
            if (DeliveredThisSecond.IsCreated) DeliveredThisSecond.Dispose();
        }
    }
}
