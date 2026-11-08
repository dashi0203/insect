using System;
using Swarm.Core;
using Swarm.Data;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Swarm.World
{
    public struct CoverParams
    {
        public float4 Affinity;
        public float CliffAffinity;
        public float MinT, MaxT, MinH, MaxH;
        public float Threshold;

        public float Suitability(float4 weights, bool cliff, float temperature, float humidity)
        {
            if (temperature < MinT || temperature > MaxT || humidity < MinH || humidity > MaxH) return 0f;
            return math.dot(weights, Affinity) + (cliff ? CliffAffinity : 0f);
        }
    }

    /// <summary>
    /// 地被（GDD 2.7）：每 Tile 存种类与覆盖度；每 coverInterval 秒跑一次元胞自动机：
    /// 适宜且 8 邻接中有同类 → +grow；不适宜 → −shrink；毒性再 −T × factor。
    /// 空 Tile：8 邻接中覆盖度 ≥ 绘制阈值最多的同类地被，若此处适宜则以 +grow 进驻。
    /// </summary>
    public sealed class GroundCoverField : IDisposable
    {
        NativeArray<CoverParams> _params;
        NativeArray<byte> _nextType;
        NativeArray<float> _nextAmount;

        public GroundCoverDef[] Defs { get; private set; }
        public int ChunkSize { get; private set; }
        public int ChunksPerSide { get; private set; }

        /// <summary>每个地形分块是否有可见变化，渲染重建后清零。</summary>
        public NativeArray<byte> DirtyChunks;

        /// <summary>每种地被覆盖度 ≥ 绘制阈值的 Tile 数（索引 = 种类 − 1）。</summary>
        public int[] VisibleTiles { get; private set; }

        public GroundCoverField(EnvGrid env, RegionDef region, GroundCoverDef[] defs, GameBalance b)
        {
            Defs = defs;
            ChunkSize = math.max(1, b.terrainChunkSize);
            ChunksPerSide = (env.Size + ChunkSize - 1) / ChunkSize;
            _params = new NativeArray<CoverParams>(defs.Length, Allocator.Persistent);
            for (int i = 0; i < defs.Length; i++)
            {
                var d = defs[i];
                _params[i] = new CoverParams
                {
                    Affinity = Biomes.Affinity(region, d.biomes),
                    CliffAffinity = d.cliffAffinity,
                    MinT = d.minTemperature, MaxT = d.maxTemperature,
                    MinH = d.minHumidity, MaxH = d.maxHumidity,
                    Threshold = d.suitabilityThreshold,
                };
            }
            _nextType = new NativeArray<byte>(env.Length, Allocator.Persistent);
            _nextAmount = new NativeArray<float>(env.Length, Allocator.Persistent);
            DirtyChunks = new NativeArray<byte>(ChunksPerSide * ChunksPerSide, Allocator.Persistent);
            VisibleTiles = new int[defs.Length];
        }

        /// <summary>初始地被：每个 Tile 取最适宜的种类，覆盖度随适宜度和噪声变化。</summary>
        public void Initialize(EnvGrid env, uint seed, GameBalance b)
        {
            var rng = new SimulationWorld(seed).CreateRandom(RandomStreams.GroundCover);
            float2 offset = rng.NextFloat2(-3000f, 3000f);
            for (int i = 0; i < env.Length; i++)
            {
                env.CoverType[i] = 0;
                env.CoverAmount[i] = 0f;
                if (env.IsWater(i) || env.IsObstacle(i)) continue;

                int best = -1;
                float bestS = 0f;
                for (int t = 0; t < _params.Length; t++)
                {
                    var p = _params[t];
                    float s = p.Suitability(env.BiomeWeights[i], env.IsCliff(i), env.Temperature[i], env.Humidity[i]);
                    if (s >= p.Threshold && s > bestS) { best = t; bestS = s; }
                }
                if (best < 0) continue;

                int2 c = env.Coord(i);
                float n = noise.cnoise((new float2(c) + offset) * 0.08f) * 0.5f + 0.5f; // 0..1 斑块
                float amount = math.saturate((bestS - _params[best].Threshold) * 1.5f + (n - 0.45f) * 1.4f);
                if (amount <= 0.02f) continue;
                env.CoverType[i] = (byte)(best + 1);
                env.CoverAmount[i] = amount;
            }
            for (int c = 0; c < DirtyChunks.Length; c++) DirtyChunks[c] = 1;
            CountVisible(env, b);
        }

        /// <summary>跑一步元胞自动机（Burst）。</summary>
        public void Step(EnvGrid env, GameBalance b)
        {
            new CoverStepJob
            {
                size = env.Size,
                chunkSize = ChunkSize,
                chunksPerSide = ChunksPerSide,
                grow = b.coverGrow,
                shrink = b.coverShrink,
                toxFactor = b.coverToxicityFactor,
                drawThreshold = b.coverDrawThreshold,
                coverParams = _params,
                type = env.CoverType,
                amount = env.CoverAmount,
                temperature = env.Temperature,
                humidity = env.Humidity,
                toxicity = env.Toxicity,
                weights = env.BiomeWeights,
                heightLevel = env.HeightLevel,
                flags = env.Flags,
                nextType = _nextType,
                nextAmount = _nextAmount,
                dirty = DirtyChunks,
            }.Schedule(env.Length, 256).Complete();

            env.CoverType.CopyFrom(_nextType);
            env.CoverAmount.CopyFrom(_nextAmount);
            CountVisible(env, b);
        }

        void CountVisible(EnvGrid env, GameBalance b)
        {
            Array.Clear(VisibleTiles, 0, VisibleTiles.Length);
            for (int i = 0; i < env.Length; i++)
            {
                int t = env.CoverType[i];
                if (t > 0 && env.CoverAmount[i] >= b.coverDrawThreshold) VisibleTiles[t - 1]++;
            }
        }

        public void Dispose()
        {
            if (_params.IsCreated) _params.Dispose();
            if (_nextType.IsCreated) _nextType.Dispose();
            if (_nextAmount.IsCreated) _nextAmount.Dispose();
            if (DirtyChunks.IsCreated) DirtyChunks.Dispose();
        }

        [BurstCompile]
        struct CoverStepJob : IJobParallelFor
        {
            public int size, chunkSize, chunksPerSide;
            public float grow, shrink, toxFactor, drawThreshold;

            [ReadOnly] public NativeArray<CoverParams> coverParams;
            [ReadOnly] public NativeArray<byte> type;
            [ReadOnly] public NativeArray<float> amount;
            [ReadOnly] public NativeArray<float> temperature;
            [ReadOnly] public NativeArray<float> humidity;
            [ReadOnly] public NativeArray<float> toxicity;
            [ReadOnly] public NativeArray<float4> weights;
            [ReadOnly] public NativeArray<byte> heightLevel;
            [ReadOnly] public NativeArray<byte> flags;

            [WriteOnly] public NativeArray<byte> nextType;
            [WriteOnly] public NativeArray<float> nextAmount;
            [NativeDisableParallelForRestriction] public NativeArray<byte> dirty;

            public void Execute(int i)
            {
                int x = i % size, y = i / size;
                int t = type[i];
                float a = amount[i];
                int newT = t;
                float newA = a;

                if ((flags[i] & (byte)(TileFlags.Water | TileFlags.Obstacle)) != 0)
                {
                    newT = 0;
                    newA = 0f;
                }
                else if (t > 0)
                {
                    var p = coverParams[t - 1];
                    bool suitable = p.Suitability(weights[i], heightLevel[i] == 1, temperature[i], humidity[i]) >= p.Threshold;
                    if (suitable)
                    {
                        if (HasNeighbor(x, y, t, 0.001f)) newA += grow;
                    }
                    else newA -= shrink;
                    newA -= toxicity[i] * toxFactor;
                    newA = math.saturate(newA);
                    if (newA <= 0f) { newT = 0; newA = 0f; }
                }
                else
                {
                    // 空 Tile：被邻近的可见地被蔓延
                    int bestType = 0, bestCount = 0;
                    for (int k = 1; k <= coverParams.Length; k++)
                    {
                        int c = CountNeighbors(x, y, k, drawThreshold);
                        if (c > bestCount) { bestCount = c; bestType = k; }
                    }
                    if (bestType > 0)
                    {
                        var p = coverParams[bestType - 1];
                        if (p.Suitability(weights[i], heightLevel[i] == 1, temperature[i], humidity[i]) >= p.Threshold
                            && toxicity[i] * toxFactor < grow)
                        {
                            newT = bestType;
                            newA = grow;
                        }
                    }
                }

                nextType[i] = (byte)newT;
                nextAmount[i] = newA;

                bool visBefore = t > 0 && a >= drawThreshold;
                bool visAfter = newT > 0 && newA >= drawThreshold;
                if (visBefore != visAfter || newT != t || (visAfter && newA != a))
                    dirty[(y / chunkSize) * chunksPerSide + x / chunkSize] = 1;
            }

            bool HasNeighbor(int x, int y, int t, float minAmount) => CountNeighbors(x, y, t, minAmount) > 0;

            int CountNeighbors(int x, int y, int t, float minAmount)
            {
                int c = 0;
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int nx = x + dx, ny = y + dy;
                    if ((uint)nx >= (uint)size || (uint)ny >= (uint)size) continue;
                    int j = ny * size + nx;
                    if (type[j] == t && amount[j] >= minAmount) c++;
                }
                return c;
            }
        }
    }
}
