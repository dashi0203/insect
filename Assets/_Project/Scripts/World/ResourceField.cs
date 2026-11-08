using System;
using System.Collections.Generic;
using Swarm.Core;
using Swarm.Data;
using Unity.Collections;
using Unity.Mathematics;
using Random = Unity.Mathematics.Random;

namespace Swarm.World
{
    /// <summary>资源实体数据（GDD 2.6 / 14.3）。普通实体不阻挡移动；巨型实体（Footprint > 1）阻挡地面单位。</summary>
    public struct ResourceNode
    {
        /// <summary>中心位置（Tile 坐标）。</summary>
        public float2 Position;
        public float Amount;
        public float Capacity;
        public float RegrowTimer;
        public float ToxicTimer;
        public float Scale;   // 个体尺寸倍率（外观）
        public ushort Chunk;
        public byte Def;
        public byte Alive;
        /// <summary>0 有储量，1 正在再生，2 等待（季节 / 气候 / 规则不允许再生）。</summary>
        public byte RegrowState;
        public byte Footprint;
        public byte Infinite;
    }

    public static class ResourceRules
    {
        /// <summary>
        /// 取空后的再生速度倍率（0 = 停止）。温度、土壤含水量、酸度、毒性共同影响（GDD 2.5 / 2.6）。
        /// </summary>
        public static float RegrowRate(ResourceNodeDef def, Season season, float temperature, float humidity,
            float soilMoisture, float acidity, float toxicity, float coldStopTemperature)
        {
            if (def.regrowMode != RegrowMode.Timer) return 0f;
            if ((def.regrowSeasons & season.ToMask()) == 0) return 0f;
            if (def.minHumidity > 0f && humidity < def.minHumidity) return 0f;
            if (!def.isPlant) return 1f - 0.5f * math.saturate(toxicity);

            float tempFactor = temperature < coldStopTemperature ? (def.stopsInCold ? 0f : 0.5f) : 1f;
            return tempFactor
                   * (0.5f + 0.5f * math.saturate(soilMoisture))
                   * (1f - 0.5f * math.saturate(acidity))
                   * (1f - math.saturate(toxicity));
        }
    }

    /// <summary>
    /// 全部资源实体 + 按 16 × 16 区块的索引（GDD 14.3）。
    /// 生成顺序：先放巨型实体（占地并写 NodeBlock），再放普通实体（成簇）。
    /// 生成后实体位置固定；枯死只置 Alive = 0，重新播种时在原位复活。
    /// </summary>
    public sealed class ResourceField : IDisposable
    {
        /// <summary>无限储量实体的“储量”显示值。</summary>
        public const float InfiniteAmount = 1e9f;

        public NativeList<ResourceNode> Nodes;
        /// <summary>每个区块在 Nodes 中的 (起始, 数量)。实体按中心所在区块归档。</summary>
        public NativeArray<int2> ChunkRanges;
        public int ChunkSize { get; private set; }
        public int ChunksPerSide { get; private set; }
        public ResourceNodeDef[] Defs { get; private set; }

        /// <summary>外观相关状态变化时递增，渲染据此重建实例矩阵。</summary>
        public int Version { get; private set; }

        public int CountFull { get; private set; }
        public int CountRegrowing { get; private set; }
        public int CountWaiting { get; private set; }
        public int CountDead { get; private set; }
        public int CountGiant { get; private set; }

        float _reseedTimer;

        public void Generate(EnvGrid env, RegionDef region, GameBalance b, ResourceNodeDef[] defs, uint seed)
        {
            Dispose();
            Defs = defs;
            ChunkSize = math.max(1, b.resourceChunkSize);
            ChunksPerSide = (env.Size + ChunkSize - 1) / ChunkSize;
            int chunkCount = ChunksPerSide * ChunksPerSide;

            for (int i = 0; i < env.Length; i++) env.Flags[i] &= unchecked((byte)~TileFlags.NodeBlock);

            var rng = new SimulationWorld(seed).CreateRandom(RandomStreams.Resources);
            var affinity = new float4[defs.Length];
            for (int d = 0; d < defs.Length; d++)
                affinity[d] = Biomes.Affinity(region, defs[d].biomes);

            BuildEcoPatches(env, b, ref rng);
            BuildZones(env, b);
            _grove = new float[env.Length];
            _scatter = b.scatterDensity;
            _ecoDensity = b.ecoPatchDensity;

            var perChunk = new List<ResourceNode>[chunkCount];
            for (int c = 0; c < chunkCount; c++) perChunk[c] = new List<ResourceNode>();

            // 两遍：巨型优先占地；放完后以巨型植物为中心生成林下地带，普通实体再避开巨型占地
            for (int pass = 0; pass < 2; pass++)
            {
            if (pass == 1) BuildGroves(env, b, perChunk);
            for (int cy = 0; cy < ChunksPerSide; cy++)
            for (int cx = 0; cx < ChunksPerSide; cx++)
            {
                int x0 = cx * ChunkSize, y0 = cy * ChunkSize;
                int x1 = math.min(env.Size, x0 + ChunkSize), y1 = math.min(env.Size, y0 + ChunkSize);
                int tiles = (x1 - x0) * (y1 - y0);

                for (int d = 0; d < defs.Length; d++)
                {
                    var def = defs[d];
                    bool giant = def.footprint > 1;
                    if (giant != (pass == 0)) continue;

                    float maxScore = (1f + def.cliffAffinity + def.waterEdgeAffinity) * (_scatter + _ecoDensity);
                    float sum = 0f;
                    for (int y = y0; y < y1; y++)
                    for (int x = x0; x < x1; x++)
                    {
                        int ti = env.Index(x, y);
                        sum += Score(env, ti, def, affinity[d]) * Density(ti, def);
                    }
                    float expected = def.densityPerChunk * sum / tiles;
                    int clusters = (int)expected + (rng.NextFloat() < expected - (int)expected ? 1 : 0);

                    for (int k = 0; k < clusters; k++)
                    {
                        for (int attempt = 0; attempt < 40; attempt++)
                        {
                            int x = rng.NextInt(x0, x1), y = rng.NextInt(y0, y1);
                            if (giant)
                            {
                                if (TryPlaceGiant(env, def, d, affinity[d], maxScore, x, y, ref rng, perChunk)) break;
                            }
                            else if (TryPlaceCluster(env, def, d, affinity[d], maxScore, x, y, ref rng, perChunk)) break;
                        }
                    }
                }
            }
            }

            Nodes = new NativeList<ResourceNode>(4096, Allocator.Persistent);
            ChunkRanges = new NativeArray<int2>(chunkCount, Allocator.Persistent);
            CountGiant = 0;
            for (int c = 0; c < chunkCount; c++)
            {
                int start = Nodes.Length;
                foreach (var node in perChunk[c])
                {
                    Nodes.Add(node);
                    if (node.Footprint > 1) CountGiant++;
                }
                ChunkRanges[c] = new int2(start, Nodes.Length - start);
            }

            _reseedTimer = 0f;
            Version++;
            CountFull = Nodes.Length;
            CountRegrowing = CountWaiting = CountDead = 0;
        }

        bool TryPlaceCluster(EnvGrid env, ResourceNodeDef def, int d, float4 aff, float maxScore, int x, int y,
            ref Random rng, List<ResourceNode>[] perChunk)
        {
            int si = env.Index(x, y);
            float s = Score(env, si, def, aff) * Density(si, def);
            if (s <= 0f || rng.NextFloat() * maxScore >= s) return false;

            var center = new float2(x + rng.NextFloat(0.2f, 0.8f), y + rng.NextFloat(0.2f, 0.8f));
            Add(env, def, d, center, ref rng, perChunk);

            int extra = rng.NextInt(math.max(1, def.clusterMin), math.max(def.clusterMin, def.clusterMax) + 1) - 1;
            if (def.isPlant) extra = (int)math.round(extra * (1f + SuperFertility(si))); // 超级营养土里一簇翻倍
            for (int e = 0; e < extra; e++)
            {
                float2 p = center + rng.NextFloat2Direction() * rng.NextFloat(0.35f, 1.25f);
                int px = (int)math.floor(p.x), py = (int)math.floor(p.y);
                if (!env.InBounds(px, py)) continue;
                if (Score(env, env.Index(px, py), def, aff) <= 0f) continue;
                Add(env, def, d, p, ref rng, perChunk);
            }
            return true;
        }

        bool TryPlaceGiant(EnvGrid env, ResourceNodeDef def, int d, float4 aff, float maxScore, int x, int y,
            ref Random rng, List<ResourceNode>[] perChunk)
        {
            int f = def.footprint;
            if (x + f > env.Size || y + f > env.Size) return false;
            int baseLevel = env.HeightLevel[env.Index(x, y)];

            float score = 0f;
            for (int dy = -1; dy <= f; dy++)
            for (int dx = -1; dx <= f; dx++)
            {
                int tx = x + dx, ty = y + dy;
                if (!env.InBounds(tx, ty)) return false;
                int i = env.Index(tx, ty);
                bool inside = dx >= 0 && dy >= 0 && dx < f && dy < f;
                // 外圈留 1 Tile 空地，避免巨型实体把山口、坡道堵死
                if (env.IsObstacle(i) || env.IsNodeBlocked(i) || env.IsRamp(i)) return false;
                if (!inside) continue;
                if (env.IsWater(i) || env.HeightLevel[i] != baseLevel) return false;
                score += Score(env, i, def, aff) * Density(i, def);
            }
            score /= f * f;
            if (score <= 0f || rng.NextFloat() * maxScore >= score) return false;

            for (int dy = 0; dy < f; dy++)
            for (int dx = 0; dx < f; dx++)
                env.Flags[env.Index(x + dx, y + dy)] |= (byte)TileFlags.NodeBlock;

            Add(env, def, d, new float2(x + f * 0.5f, y + f * 0.5f), ref rng, perChunk);
            return true;
        }

        void Add(EnvGrid env, ResourceNodeDef def, int d, float2 pos, ref Random rng, List<ResourceNode>[] perChunk)
        {
            int cx = math.clamp((int)(pos.x / ChunkSize), 0, ChunksPerSide - 1);
            int cy = math.clamp((int)(pos.y / ChunkSize), 0, ChunksPerSide - 1);
            int chunk = cy * ChunksPerSide + cx;
            float capacity = def.infinite ? InfiniteAmount : def.amount;
            perChunk[chunk].Add(new ResourceNode
            {
                Position = pos,
                Amount = capacity,
                Capacity = capacity,
                Scale = rng.NextFloat(def.sizeJitter.x, math.max(def.sizeJitter.x, def.sizeJitter.y)),
                Chunk = (ushort)chunk,
                Def = (byte)d,
                Alive = 1,
                Footprint = (byte)math.max(1, def.footprint),
                Infinite = (byte)(def.infinite ? 1 : 0),
            });
        }

        // ---------------------------------------------------------------- ecological patches

        float[] _patch, _waterBelt, _cliffFoot, _grove;
        float _scatter = 1f, _ecoDensity;
        float _waterBeltStrength, _cliffFootStrength, _groveStrength, _habitatStrength;
        float _superPeak = 1.5f, _superBoost;
        EnvGrid _env;

        /// <summary>
        /// 资源密度倍率 = scatter + ecoDensity × 地带强度。地带按规则分配给不同资源：
        /// 随机斑块 → 全部；水边植被带 → 植物与喜水资源；崖脚碎石带 → 矿物；巨木林下 → 普通植物。
        /// </summary>
        float Density(int i, ResourceNodeDef def)
        {
            if (_patch == null) return 1f;
            float zone;
            if (def.isPlant)
            {
                // 植物：按自己的生境（含水量 + 肥力）成团，密度再乘以肥力：
                // 普通区 ×肥力（贫瘠处稀少）；超级营养土 ×(1 + 超出部分 × boost)，并放宽生境要求
                float super01 = SuperFertility(i);
                float fertScale = super01 > 0f ? 1f + super01 * _superBoost : math.saturate(_env.Fertility[i]);
                zone = Habitat(_env, i, def, super01 * 0.8f) * _habitatStrength * fertScale;
                if (def.waterEdgeAffinity > 0f) zone = math.max(zone, _waterBelt[i] * _waterBeltStrength);
                if (def.footprint <= 1) zone = math.max(zone, _grove[i] * _groveStrength * Habitat(_env, i, def, 0.5f));
            }
            else
            {
                zone = _patch[i];
                if (def.waterEdgeAffinity > 0f) zone = math.max(zone, _waterBelt[i] * _waterBeltStrength);
                if (def.kind == ResourceKind.Mineral) zone = math.max(zone, _cliffFoot[i] * _cliffFootStrength);
            }
            return _scatter + _ecoDensity * zone;
        }

        /// <summary>
        /// 植物生境 0..1：土壤含水量落在 [moistureMin, moistureMax] 内、肥力 ≥ fertilityMin。边界用 smoothstep 柔化。
        /// floor &gt; 0 时生境不足也保留一部分（林下带用：巨木下不完全合适的植物也能少量出现）。
        /// </summary>
        /// <summary>超级营养土程度 0..1：肥力 1 → 0，峰值 → 1。</summary>
        public float SuperFertility(int i) => math.saturate((_env.Fertility[i] - 1f) / (_superPeak - 1f));

        public static float Habitat(EnvGrid env, int i, ResourceNodeDef def, float floor = 0f)
        {
            const float soft = 0.08f;
            float m = env.SoilMoisture[i];
            float wet = math.smoothstep(def.moistureMin - soft, def.moistureMin + soft, m)
                        * (1f - math.smoothstep(def.moistureMax - soft, def.moistureMax + soft, m));
            float fert = math.smoothstep(def.fertilityMin - soft, def.fertilityMin + soft * 1.5f, env.Fertility[i]);
            return math.max(floor, wet * fert);
        }

        /// <summary>
        /// 规则地带：水边植被带（离水 1..waterBeltWidth，越近越茂密，边缘用噪声打散）、
        /// 崖脚碎石带（悬崖或高山脚下的陆地，cliffFootWidth 格内）。
        /// </summary>
        void BuildZones(EnvGrid env, GameBalance b)
        {
            _waterBelt = new float[env.Length];
            _cliffFoot = new float[env.Length];
            _waterBeltStrength = b.waterBeltStrength;
            _cliffFootStrength = b.cliffFootStrength;
            _groveStrength = b.groveStrength;
            _habitatStrength = b.habitatStrength;
            _superPeak = math.max(1.01f, b.superFertilePeak);
            _superBoost = b.superFertileDensityBoost;
            _env = env;

            int beltW = math.max(1, b.waterBeltWidth);
            for (int i = 0; i < env.Length; i++)
            {
                if (env.IsWater(i) || env.IsObstacle(i)) continue;
                int wd = env.WaterDistance[i];
                if (wd < 1 || wd > beltW) continue;
                int2 c = env.Coord(i);
                float wobble = 0.75f + 0.25f * noise.cnoise(new float2(c) * 0.2f);
                _waterBelt[i] = math.saturate((1f - (wd - 1f) / beltW) * wobble * 1.15f);
            }

            // 崖脚：陆地 Tile 到最近悬崖 / 高山的切比雪夫距离
            int footW = math.max(1, b.cliffFootWidth);
            for (int y = 0; y < env.Size; y++)
            for (int x = 0; x < env.Size; x++)
            {
                int i = env.Index(x, y);
                if (env.HeightLevel[i] != 0 || env.IsWater(i) || env.IsObstacle(i)) continue;
                int best = int.MaxValue;
                for (int dy = -footW; dy <= footW; dy++)
                for (int dx = -footW; dx <= footW; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (!env.InBounds(nx, ny)) continue;
                    int j = env.Index(nx, ny);
                    if (env.HeightLevel[j] == 1 || env.IsObstacle(j))
                        best = math.min(best, math.max(math.abs(dx), math.abs(dy)));
                }
                if (best <= footW) _cliffFoot[i] = 1f - (best - 1f) / (footW + 1f);
            }
        }

        /// <summary>巨木林下：以已放置的巨型植物为中心，groveRadius 内的强度随距离衰减。</summary>
        void BuildGroves(EnvGrid env, GameBalance b, List<ResourceNode>[] perChunk)
        {
            float r = math.max(1f, b.groveRadius);
            int ri = (int)math.ceil(r);
            foreach (var list in perChunk)
            foreach (var node in list)
            {
                if (node.Footprint <= 1 || !Defs[node.Def].isPlant) continue;
                int cx = (int)node.Position.x, cy = (int)node.Position.y;
                for (int dy = -ri; dy <= ri; dy++)
                for (int dx = -ri; dx <= ri; dx++)
                {
                    int tx = cx + dx, ty = cy + dy;
                    if (!env.InBounds(tx, ty)) continue;
                    float d = math.length(new float2(tx + 0.5f, ty + 0.5f) - node.Position);
                    float s = 1f - math.smoothstep(r * 0.35f, r, d);
                    int ti = env.Index(tx, ty);
                    _grove[ti] = math.max(_grove[ti], s);
                }
            }
        }

        /// <summary>斑块强度 0..1（仅生成时使用），可在调试中显示。</summary>
        public float PatchStrength(int i) => _patch == null ? 0f : _patch[i];

        /// <summary>
        /// 生态斑块：资源成片成群出现，而不是均匀撒开。斑块中心偏好水边（1–5 Tile），
        /// 半径随机，边缘用噪声打散成不规则形状；斑块内各种资源（含巨型实体）按各自群系偏好混居。
        /// </summary>
        void BuildEcoPatches(EnvGrid env, GameBalance b, ref Random rng)
        {
            _patch = new float[env.Length];
            int count = (int)math.round(env.Length / 4096f * b.ecoPatchesPer64);
            float maxWeight = 1f + b.ecoPatchWaterBias;
            float2 noiseOffset = rng.NextFloat2(-2000f, 2000f);
            int placed = 0;
            for (int attempt = 0; attempt < count * 60 && placed < count; attempt++)
            {
                int x = rng.NextInt(0, env.Size), y = rng.NextInt(0, env.Size);
                int i = env.Index(x, y);
                if (env.IsObstacle(i) || env.IsWater(i)) continue;
                int wd = env.WaterDistance[i];
                float w = 1f + (wd >= 1 && wd <= 5 ? b.ecoPatchWaterBias : 0f);
                if (rng.NextFloat() * maxWeight >= w) continue;

                float r = rng.NextFloat(b.ecoPatchRadiusMin, math.max(b.ecoPatchRadiusMin, b.ecoPatchRadiusMax));
                int ri = (int)math.ceil(r * 1.3f);
                for (int dy = -ri; dy <= ri; dy++)
                for (int dx = -ri; dx <= ri; dx++)
                {
                    int tx = x + dx, ty = y + dy;
                    if (!env.InBounds(tx, ty)) continue;
                    float edge = r * (0.8f + 0.4f * noise.cnoise((new float2(tx, ty) + noiseOffset) * 0.15f));
                    float d = math.sqrt(dx * dx + dy * dy);
                    float strength = 1f - math.smoothstep(edge * 0.45f, edge, d);
                    int ti = env.Index(tx, ty);
                    _patch[ti] = math.max(_patch[ti], strength);
                }
                placed++;
            }
        }

        static float Score(EnvGrid env, int i, ResourceNodeDef def, float4 affinity)
        {
            if (env.IsWater(i) || env.IsObstacle(i) || env.IsNodeBlocked(i)) return 0f;
            float s = math.dot(env.BiomeWeights[i], affinity);
            if (env.IsCliff(i)) s += def.cliffAffinity;
            if (env.WaterDistance[i] <= 2) s += def.waterEdgeAffinity;
            return s;
        }

        /// <summary>1 Hz：再生、毒性枯死、重新播种（GDD 2.6）。</summary>
        public void Tick(EnvGrid env, GameBalance b, Season season, float dt)
        {
            int full = 0, regrowing = 0, waiting = 0, dead = 0;
            bool changed = false;

            for (int n = 0; n < Nodes.Length; n++)
            {
                var node = Nodes[n];
                var def = Defs[node.Def];
                int i = TileIndex(env, node.Position);

                if (node.Alive != 0 && def.isPlant)
                {
                    if (env.Toxicity[i] > b.plantToxicDeathThreshold)
                    {
                        node.ToxicTimer += dt;
                        if (node.ToxicTimer >= b.plantToxicDeathSeconds)
                        {
                            node.Alive = 0;
                            node.Amount = 0f;
                            node.RegrowTimer = 0f;
                            changed = true;
                        }
                    }
                    else node.ToxicTimer = 0f;
                }

                if (node.Alive == 0) { dead++; Nodes[n] = node; continue; }

                if (node.Infinite != 0 || node.Amount > 0f)
                {
                    node.RegrowState = 0;
                    full++;
                }
                else
                {
                    float rate = ResourceRules.RegrowRate(def, season, env.Temperature[i], env.Humidity[i],
                        env.SoilMoisture[i], env.Acidity[i], env.Toxicity[i], b.coldStopTemperature);
                    if (rate > 0f)
                    {
                        node.RegrowState = 1;
                        node.RegrowTimer += dt * rate;
                        if (node.RegrowTimer >= def.regrowSeconds)
                        {
                            node.Amount = node.Capacity;
                            node.RegrowTimer = 0f;
                            node.RegrowState = 0;
                            changed = true;
                        }
                    }
                    else node.RegrowState = 2;

                    if (node.RegrowState == 1) regrowing++;
                    else if (node.RegrowState == 2) waiting++;
                    else full++;
                }
                Nodes[n] = node;
            }

            _reseedTimer += dt;
            if (_reseedTimer >= b.reseedIntervalSeconds)
            {
                _reseedTimer = 0f;
                changed |= Reseed(env, b);
            }

            CountFull = full; CountRegrowing = regrowing; CountWaiting = waiting; CountDead = dead;
            if (changed) Version++;
        }

        bool Reseed(EnvGrid env, GameBalance b)
        {
            bool any = false;
            for (int c = 0; c < ChunkRanges.Length; c++)
            {
                int2 r = ChunkRanges[c];
                int deadIndex = -1;
                for (int n = r.x; n < r.x + r.y; n++)
                    if (Nodes[n].Alive == 0 && Defs[Nodes[n].Def].isPlant) { deadIndex = n; break; }
                if (deadIndex < 0) continue;
                if (ChunkToxicity(env, c) >= b.reseedToxicityThreshold) continue;

                var node = Nodes[deadIndex];
                node.Alive = 1;
                node.Amount = node.Infinite != 0 ? node.Capacity : 0f; // 新芽从零开始再生
                node.RegrowTimer = 0f;
                node.ToxicTimer = 0f;
                Nodes[deadIndex] = node;
                any = true;
            }
            return any;
        }

        float ChunkToxicity(EnvGrid env, int chunk)
        {
            int cx = chunk % ChunksPerSide, cy = chunk / ChunksPerSide;
            int x0 = cx * ChunkSize, y0 = cy * ChunkSize;
            int x1 = math.min(env.Size, x0 + ChunkSize), y1 = math.min(env.Size, y0 + ChunkSize);
            float sum = 0f;
            for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
                sum += env.Toxicity[env.Index(x, y)];
            return sum / ((x1 - x0) * (y1 - y0));
        }

        /// <summary>进入新季节时调用：秋季刷新落叶堆。</summary>
        public void OnSeasonChanged(Season season)
        {
            if (season != Season.Autumn) return;
            bool changed = false;
            for (int n = 0; n < Nodes.Length; n++)
            {
                var node = Nodes[n];
                if (node.Alive == 0 || Defs[node.Def].regrowMode != RegrowMode.AutumnRefresh) continue;
                node.Amount = node.Capacity;
                node.RegrowState = 0;
                Nodes[n] = node;
                changed = true;
            }
            if (changed) Version++;
        }

        /// <summary>调试：取空全部存活的有限实体，用来观察再生。无限实体不受影响。</summary>
        public void DepleteAll()
        {
            for (int n = 0; n < Nodes.Length; n++)
            {
                var node = Nodes[n];
                if (node.Alive == 0 || node.Infinite != 0) continue;
                node.Amount = 0f;
                node.RegrowTimer = 0f;
                Nodes[n] = node;
            }
            Version++;
        }

        public static int TileIndex(EnvGrid env, float2 pos)
        {
            int x = math.clamp((int)pos.x, 0, env.Size - 1);
            int y = math.clamp((int)pos.y, 0, env.Size - 1);
            return env.Index(x, y);
        }

        public void Dispose()
        {
            if (Nodes.IsCreated) Nodes.Dispose();
            if (ChunkRanges.IsCreated) ChunkRanges.Dispose();
        }
    }
}
