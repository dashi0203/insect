using System.Collections.Generic;
using Swarm.Core;
using Swarm.Data;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Random = Unity.Mathematics.Random;

namespace Swarm.World
{
    /// <summary>
    /// 世界生成（GDD 2.2 / 2.4）：同一种子 + 同一区域 → 同一张地图。
    /// 1) Burst Job：温度 / 湿度 / 高度 / 土壤噪声 + 群系权重
    /// 2) 高山（山脊噪声 × 区域遮罩 + 地图边缘山带）：硬性障碍，地面与空中单位都无法逾越
    /// 3) 悬崖（高度 > 阈值），去掉过小的悬崖块，沿边缘生成 2–3 Tile 宽的坡道
    /// 4) 水面；连通性修复（被隔开的小块填成山，大块开山口）；水距离、土壤含水量、山体深度
    /// </summary>
    public static class WorldGen
    {
        static readonly int2[] Dir4 = { new int2(1, 0), new int2(-1, 0), new int2(0, 1), new int2(0, -1) };

        public static void Generate(EnvGrid env, RegionDef region, GameBalance b, uint seed)
        {
            var rng = new SimulationWorld(seed).CreateRandom(RandomStreams.WorldGen);

            int count = Biomes.Count(region);
            float tSpan = math.max(1e-3f, b.temperatureNormMax - b.temperatureNormMin);
            float4 cT = 0, cH = 0;
            for (int i = 0; i < count; i++)
            {
                cT[i] = (region.biomes[i].centerTemperature - b.temperatureNormMin) / tSpan;
                cH[i] = region.biomes[i].centerHumidity;
            }

            var mountainField = new NativeArray<float>(env.Length, Allocator.TempJob);
            var job = new BaseFieldsJob
            {
                size = env.Size,
                frequency = b.noiseFrequency,
                octaves = math.max(1, b.noiseOctaves),
                contrast = b.noiseContrast,
                offsetTemp = rng.NextFloat2(-4000f, 4000f),
                offsetHum = rng.NextFloat2(-4000f, 4000f),
                offsetHeight = rng.NextFloat2(-4000f, 4000f),
                offsetSoil = rng.NextFloat2(-4000f, 4000f),
                offsetMountain = rng.NextFloat2(-4000f, 4000f),
                offsetMask = rng.NextFloat2(-4000f, 4000f),
                mountainFrequency = b.mountainFrequency,
                mountainMaskThreshold = b.mountainMaskThreshold,
                borderWidth = b.borderMountainWidth,
                baseTemp = region.baseTemperature,
                baseHum = region.baseHumidity,
                baseAcid = region.baseAcidity,
                tempRange = b.temperatureOffset,
                humRange = b.humidityOffset,
                tempNormMin = b.temperatureNormMin,
                tempNormSpan = tSpan,
                sigma = b.biomeSigma,
                centerT = cT,
                centerH = cH,
                biomeCount = count,
                temperatureBase = env.TemperatureBase,
                humidity = env.Humidity,
                heightNoise = env.HeightNoise,
                soilNoise = env.SoilMoisture, // 先放原始噪声，ComputeSoil 再换算
                fertilityNoise = env.Fertility,
                soilFrequency = b.soilNoiseFrequency,
                fertilityFrequency = b.fertilityNoiseFrequency,
                acidity = env.Acidity,
                weights = env.BiomeWeights,
                mountain = mountainField,
            };
            job.Schedule(env.Length, 256).Complete();

            for (int i = 0; i < env.Length; i++)
            {
                env.Flags[i] = 0;
                env.Toxicity[i] = 0f;
                env.CoverType[i] = 0;
                env.CoverAmount[i] = 0f;
            }

            BuildMountains(env, b, mountainField);
            mountainField.Dispose();
            BuildCliffs(env, b, ref rng);
            BuildWater(env, b);
            EnsureConnectivity(env, b);
            ComputeWaterDistance(env);
            ComputeObstacleDepth(env);
            ComputeSoil(env, b);
            ComputeFertility(env, b);
            BuildSuperFertile(env, b, ref rng);
            env.Temperature.CopyFrom(env.TemperatureBase);
        }

        // ---------------------------------------------------------------- mountains

        static void BuildMountains(EnvGrid env, GameBalance b, NativeArray<float> field)
        {
            int n = env.Length;
            var core = new bool[n];
            for (int i = 0; i < n; i++) core[i] = field[i] > b.mountainThreshold;

            // 向外膨胀（圆形核）：加粗山脉，把相近的山段连成一片
            int r = math.max(0, b.mountainDilate);
            int size = env.Size;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                bool hit = false;
                for (int dy = -r; dy <= r && !hit; dy++)
                for (int dx = -r; dx <= r && !hit; dx++)
                {
                    if (dx * dx + dy * dy > r * r + r) continue;
                    int nx = x + dx, ny = y + dy;
                    if ((uint)nx >= (uint)size || (uint)ny >= (uint)size) continue;
                    hit = core[ny * size + nx];
                }
                if (hit) env.Flags[y * size + x] |= (byte)TileFlags.Obstacle;
            }

            // 去掉零碎的小山包
            var mark = new bool[n];
            var members = new List<int>();
            for (int i = 0; i < n; i++)
            {
                if (mark[i] || !env.IsObstacle(i)) continue;
                Flood(env, i, mark, members, env.IsObstacle);
                if (members.Count >= b.minMountainTiles) continue;
                foreach (int t in members) env.Flags[t] &= unchecked((byte)~TileFlags.Obstacle);
            }
        }

        /// <summary>
        /// 保证地面单位能到达全部可走区域：取最大的连通区为主区；
        /// 其他区域小于 minPocketTiles 的直接填成山，否则沿最短路径穿过山 / 水开一条 passWidth 宽的山口。
        /// 悬崖不算阻挡（坡道或攀爬前肢可通过）。
        /// </summary>
        static void EnsureConnectivity(EnvGrid env, GameBalance b)
        {
            int n = env.Length;
            for (int guard = 0; guard < 64; guard++)
            {
                var comp = new int[n];
                for (int i = 0; i < n; i++) comp[i] = -1;
                var sizes = new List<int>();
                var mark = new bool[n];
                var members = new List<int>();
                for (int i = 0; i < n; i++)
                {
                    if (mark[i] || !env.IsGroundWalkable(i)) continue;
                    Flood(env, i, mark, members, env.IsGroundWalkable);
                    foreach (int t in members) comp[t] = sizes.Count;
                    sizes.Add(members.Count);
                }
                if (sizes.Count <= 1) return;

                int main = 0;
                for (int c = 1; c < sizes.Count; c++) if (sizes[c] > sizes[main]) main = c;

                bool filled = false;
                for (int c = 0; c < sizes.Count; c++)
                {
                    if (c == main || sizes[c] >= b.minPocketTiles) continue;
                    for (int i = 0; i < n; i++)
                        if (comp[i] == c) env.Flags[i] |= (byte)TileFlags.Obstacle;
                    filled = true;
                }
                if (filled) continue; // 重新统计后再处理大区域

                int target = main == 0 ? 1 : 0;
                CarvePass(env, b, comp, target, main);
            }
        }

        static void CarvePass(EnvGrid env, GameBalance b, int[] comp, int from, int to)
        {
            int n = env.Length;
            var prev = new int[n];
            for (int i = 0; i < n; i++) prev[i] = -2;
            var q = new Queue<int>();
            for (int i = 0; i < n; i++)
                if (comp[i] == from) { prev[i] = -1; q.Enqueue(i); }

            int hit = -1;
            while (q.Count > 0 && hit < 0)
            {
                int t = q.Dequeue();
                int2 p = env.Coord(t);
                for (int d = 0; d < 4; d++)
                {
                    int2 o = p + Dir4[d];
                    if (!env.InBounds(o)) continue;
                    int oi = env.Index(o);
                    if (prev[oi] != -2) continue;
                    prev[oi] = t;
                    if (comp[oi] == to) { hit = oi; break; }
                    q.Enqueue(oi);
                }
            }
            if (hit < 0) return;

            int r = math.max(0, (b.passWidth - 1) / 2);
            for (int t = prev[hit]; t >= 0 && comp[t] != from; t = prev[t])
            {
                int2 p = env.Coord(t);
                for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    int2 c = p + new int2(dx, dy);
                    if (!env.InBounds(c)) continue;
                    int ci = env.Index(c);
                    env.Flags[ci] &= unchecked((byte)~(TileFlags.Obstacle | TileFlags.Water));
                }
            }
        }

        static void ComputeObstacleDepth(EnvGrid env)
        {
            int n = env.Length;
            var q = new Queue<int>();
            for (int i = 0; i < n; i++)
            {
                if (!env.IsObstacle(i)) { env.ObstacleDepth[i] = 0; q.Enqueue(i); }
                else env.ObstacleDepth[i] = 255;
            }
            while (q.Count > 0)
            {
                int t = q.Dequeue();
                int nd = env.ObstacleDepth[t] + 1;
                if (nd >= 255) continue;
                int2 p = env.Coord(t);
                for (int d = 0; d < 4; d++)
                {
                    int2 o = p + Dir4[d];
                    if (!env.InBounds(o)) continue;
                    int oi = env.Index(o);
                    if (env.ObstacleDepth[oi] <= nd) continue;
                    env.ObstacleDepth[oi] = (byte)nd;
                    q.Enqueue(oi);
                }
            }
            for (int i = 0; i < n; i++)
                if (env.ObstacleDepth[i] == 255) env.ObstacleDepth[i] = 32; // 全是山的极端情况
        }

        // ---------------------------------------------------------------- cliffs & ramps

        static void BuildCliffs(EnvGrid env, GameBalance b, ref Random rng)
        {
            int n = env.Length;
            for (int i = 0; i < n; i++)
                env.HeightLevel[i] = (byte)(env.HeightNoise[i] > b.cliffThreshold && !env.IsObstacle(i) ? 1 : 0);

            var mark = new bool[n];
            var members = new List<int>();
            for (int i = 0; i < n; i++)
            {
                if (mark[i] || env.HeightLevel[i] != 1) continue;
                Flood(env, i, mark, members, t => env.HeightLevel[t] == 1);
                if (members.Count < b.minCliffRegionTiles)
                {
                    foreach (int t in members) env.HeightLevel[t] = 0;
                    continue;
                }
                BuildRamps(env, b, members, ref rng);
            }
        }

        static void BuildRamps(EnvGrid env, GameBalance b, List<int> members, ref Random rng)
        {
            var edges = new List<int2>(); // x = tile, y = dir
            foreach (int t in members)
            {
                int2 p = env.Coord(t);
                for (int d = 0; d < 4; d++)
                {
                    int2 o = p + Dir4[d];
                    if (env.InBounds(o) && env.HeightLevel[env.Index(o)] == 0 && !env.IsObstacle(env.Index(o)))
                        edges.Add(new int2(t, d));
                }
            }
            if (edges.Count == 0) return;

            int ramps = math.clamp(edges.Count / math.max(1, b.rampEdgeTilesPerRamp), 1, math.max(1, b.maxRampsPerCliff));
            // 坡道只放在笔直的崖边上：整段宽度（含两端各 1 格）都是同向的崖边才放，否则换位置重试。
            // 这样不会在锯齿状轮廓上留下 1 格宽的碎坡道和三角形侧壁。
            for (int r = 0; r < ramps; r++)
            {
                for (int attempt = 0; attempt < 30; attempt++)
                {
                    int2 e = edges[rng.NextInt(edges.Count)];
                    int2 dir = Dir4[e.y];
                    int2 tangent = new int2(-dir.y, dir.x);
                    int width = rng.NextInt(b.rampWidthMin, b.rampWidthMax + 1);
                    int2 p0 = env.Coord(e.x) - tangent * ((width - 1) / 2);

                    bool ok = true;
                    for (int k = -1; k <= width && ok; k++)
                        ok = IsStraightCliffEdge(env, p0 + tangent * k, dir);
                    if (!ok) continue;

                    for (int k = 0; k < width; k++)
                    {
                        int2 p = p0 + tangent * k;
                        env.Flags[env.Index(p)] |= (byte)TileFlags.Ramp;
                        env.Flags[env.Index(p - dir)] |= (byte)TileFlags.Ramp;
                    }
                    break;
                }
            }
        }

        /// <summary>p 是悬崖、外侧 p + dir 是普通陆地、内侧 p − dir 也是悬崖，且都不是坡道 / 高山。</summary>
        static bool IsStraightCliffEdge(EnvGrid env, int2 p, int2 dir)
        {
            int2 outside = p + dir, inner = p - dir;
            if (!env.InBounds(p) || !env.InBounds(outside) || !env.InBounds(inner)) return false;
            int i = env.Index(p), o = env.Index(outside), n = env.Index(inner);
            return env.HeightLevel[i] == 1 && env.HeightLevel[n] == 1 && env.HeightLevel[o] == 0
                   && !env.IsObstacle(i) && !env.IsObstacle(n) && !env.IsObstacle(o)
                   && !env.IsRamp(i) && !env.IsRamp(n);
        }

        // ---------------------------------------------------------------- water & soil

        static void BuildWater(EnvGrid env, GameBalance b)
        {
            int n = env.Length;
            for (int i = 0; i < n; i++)
            {
                if (env.HeightLevel[i] == 0 && !env.IsObstacle(i) && env.HeightNoise[i] < b.waterThreshold && env.Humidity[i] > b.waterMinHumidity
                    && !TouchesRamp(env, i))
                    env.Flags[i] |= (byte)TileFlags.Water;
            }

            var mark = new bool[n];
            var members = new List<int>();
            for (int i = 0; i < n; i++)
            {
                if (mark[i] || !env.IsWater(i)) continue;
                Flood(env, i, mark, members, env.IsWater);
                if (members.Count >= b.minWaterRegionTiles) continue;
                foreach (int t in members) env.Flags[t] &= unchecked((byte)~TileFlags.Water);
            }
        }

        static bool TouchesRamp(EnvGrid env, int i)
        {
            int2 p = env.Coord(i);
            for (int d = 0; d < 4; d++)
            {
                int2 o = p + Dir4[d];
                if (env.InBounds(o) && env.IsRamp(env.Index(o))) return true;
            }
            return false;
        }

        static void ComputeWaterDistance(EnvGrid env)
        {
            int n = env.Length;
            var q = new Queue<int>();
            for (int i = 0; i < n; i++)
            {
                if (env.IsWater(i)) { env.WaterDistance[i] = 0; q.Enqueue(i); }
                else env.WaterDistance[i] = 255;
            }
            while (q.Count > 0)
            {
                int t = q.Dequeue();
                int nd = env.WaterDistance[t] + 1;
                if (nd >= 255) continue;
                int2 p = env.Coord(t);
                for (int d = 0; d < 4; d++)
                {
                    int2 o = p + Dir4[d];
                    if (!env.InBounds(o)) continue;
                    int oi = env.Index(o);
                    if (env.WaterDistance[oi] <= nd) continue;
                    env.WaterDistance[oi] = (byte)nd;
                    q.Enqueue(oi);
                }
            }
        }

        /// <summary>
        /// 营养土：中频噪声成片（斑块状），水边与低地更肥，悬崖顶贫瘠；再提高对比度，让肥沃区与贫瘠区界线清楚。
        /// </summary>
        static void ComputeFertility(EnvGrid env, GameBalance b)
        {
            for (int i = 0; i < env.Length; i++)
            {
                if (env.IsWater(i) || env.IsObstacle(i)) { env.Fertility[i] = 0f; continue; }
                float n = env.Fertility[i]; // -1..1 原始噪声
                int wd = env.WaterDistance[i];
                float nearWater = wd <= 3 ? (4 - wd) * 0.06f : 0f;
                float cliff = env.HeightLevel[i] == 1 ? -0.2f : 0f;
                float f = 0.5f + n * 0.5f * b.fertilityContrast + nearWater + cliff;
                env.Fertility[i] = math.saturate(f);
            }
        }

        /// <summary>
        /// 超级营养土（肥力 &gt; 1，即 &gt; 100）：少数几个热点，中心 = superFertilePeak，向外平滑回落到原肥力。
        /// 中心偏好本来就肥沃、非悬崖的陆地；边缘用噪声打散成不规则形状。
        /// </summary>
        static void BuildSuperFertile(EnvGrid env, GameBalance b, ref Random rng)
        {
            int count = (int)math.round(env.Length / (96f * 96f) * b.superFertilePer96);
            float2 noiseOffset = rng.NextFloat2(-3000f, 3000f);
            int placed = 0;
            for (int attempt = 0; attempt < count * 80 && placed < count; attempt++)
            {
                int x = rng.NextInt(0, env.Size), y = rng.NextInt(0, env.Size);
                int i = env.Index(x, y);
                if (!env.IsGroundWalkable(i) || env.IsCliff(i)) continue;
                if (rng.NextFloat() > env.Fertility[i]) continue; // 越肥越容易成为热点

                float r = rng.NextFloat(b.superFertileRadiusMin, math.max(b.superFertileRadiusMin, b.superFertileRadiusMax));
                int ri = (int)math.ceil(r * 1.3f);
                for (int dy = -ri; dy <= ri; dy++)
                for (int dx = -ri; dx <= ri; dx++)
                {
                    int tx = x + dx, ty = y + dy;
                    if (!env.InBounds(tx, ty)) continue;
                    int ti = env.Index(tx, ty);
                    if (env.IsWater(ti) || env.IsObstacle(ti)) continue;
                    float edge = r * (0.75f + 0.45f * noise.cnoise((new float2(tx, ty) + noiseOffset) * 0.18f));
                    float d = math.sqrt(dx * dx + dy * dy);
                    float s = 1f - math.smoothstep(edge * 0.25f, edge, d);
                    if (s <= 0f) continue;
                    env.Fertility[ti] = math.max(env.Fertility[ti], math.lerp(env.Fertility[ti], b.superFertilePeak, s));
                }
                placed++;
            }
        }

        static void ComputeSoil(EnvGrid env, GameBalance b)
        {
            for (int i = 0; i < env.Length; i++)
            {
                if (env.IsWater(i)) { env.SoilMoisture[i] = 1f; continue; }
                float noise = env.SoilMoisture[i]; // -1..1
                int dist = env.WaterDistance[i];
                float nearWater = dist <= 3 ? (4 - dist) * 0.07f : 0f;
                float cliff = env.HeightLevel[i] == 1 ? -0.1f : 0f;
                // 湿度给基调，中频噪声造出干湿交错的斑块（干旱图）
                env.SoilMoisture[i] = math.saturate(env.Humidity[i] * b.soilHumidityWeight + noise * b.soilNoiseAmplitude
                                                    + nearWater * 1.5f + cliff);
            }
        }

        // ---------------------------------------------------------------- helpers

        static void Flood(EnvGrid env, int start, bool[] mark, List<int> members, System.Func<int, bool> pred)
        {
            members.Clear();
            var q = new Queue<int>();
            q.Enqueue(start);
            mark[start] = true;
            while (q.Count > 0)
            {
                int t = q.Dequeue();
                members.Add(t);
                int2 p = env.Coord(t);
                for (int d = 0; d < 4; d++)
                {
                    int2 o = p + Dir4[d];
                    if (!env.InBounds(o)) continue;
                    int oi = env.Index(o);
                    if (mark[oi] || !pred(oi)) continue;
                    mark[oi] = true;
                    q.Enqueue(oi);
                }
            }
        }

        [BurstCompile]
        struct BaseFieldsJob : IJobParallelFor
        {
            public int size;
            public float frequency;
            public int octaves;
            public float contrast;
            public float2 offsetTemp, offsetHum, offsetHeight, offsetSoil, offsetMountain, offsetMask;
            public float mountainFrequency, mountainMaskThreshold, borderWidth;
            public float baseTemp, baseHum, baseAcid, tempRange, humRange;
            public float tempNormMin, tempNormSpan, sigma;
            public float4 centerT, centerH;
            public int biomeCount;

            [WriteOnly] public NativeArray<float> temperatureBase;
            [WriteOnly] public NativeArray<float> humidity;
            [WriteOnly] public NativeArray<float> heightNoise;
            [WriteOnly] public NativeArray<float> soilNoise;
            [WriteOnly] public NativeArray<float> fertilityNoise;
            public float soilFrequency, fertilityFrequency;
            [WriteOnly] public NativeArray<float> acidity;
            [WriteOnly] public NativeArray<float4> weights;
            [WriteOnly] public NativeArray<float> mountain;

            public void Execute(int i)
            {
                float2 p = new float2(i % size, i / size);
                float nT = Fbm(p, offsetTemp);
                float nH = Fbm(p, offsetHum);
                float nZ = Fbm(p, offsetHeight);
                float nS = FbmAt(p, offsetSoil, soilFrequency);
                fertilityNoise[i] = FbmAt(p, offsetSoil + new float2(1234.5f, -987.6f), fertilityFrequency);

                float t = baseTemp + nT * tempRange;
                float h = math.saturate(baseHum + nH * humRange);
                temperatureBase[i] = t;
                humidity[i] = h;
                heightNoise[i] = nZ * 0.5f + 0.5f;
                soilNoise[i] = nS;
                acidity[i] = math.saturate(baseAcid + nS * 0.05f);
                weights[i] = Biomes.Weights((t - tempNormMin) / tempNormSpan, h, centerT, centerH, biomeCount, sigma);

                // 山脊噪声 (1 - |n|)^2，只在低频遮罩为正的区域出现成片山脉
                float ridge = 0f, amp = 1f, norm = 0f, f = mountainFrequency;
                for (int k = 0; k < 3; k++)
                {
                    float r = 1f - math.abs(noise.cnoise((p + offsetMountain) * f));
                    ridge += amp * r * r;
                    norm += amp;
                    amp *= 0.5f;
                    f *= 2f;
                }
                ridge /= norm;
                float mask = math.smoothstep(mountainMaskThreshold - 0.15f, mountainMaskThreshold + 0.25f,
                    noise.cnoise((p + offsetMask) * mountainFrequency * 0.45f));

                // 地图边缘山带：宽度随噪声起伏，不是一圈整齐的墙
                float edge = math.min(math.min(p.x, p.y), math.min(size - 1 - p.x, size - 1 - p.y));
                float wobble = 0.55f + 0.45f * noise.cnoise((p + offsetMask) * 0.06f);
                float border = math.saturate(1f - edge / math.max(1f, borderWidth * wobble));
                mountain[i] = math.max(ridge * mask, border);
            }

            float Fbm(float2 p, float2 offset) => FbmAt(p, offset, frequency);

            /// <summary>fBm Perlin，输出放大后夹到 [-1, 1]。</summary>
            float FbmAt(float2 p, float2 offset, float baseFrequency)
            {
                float sum = 0f, amp = 1f, norm = 0f, f = baseFrequency;
                for (int k = 0; k < octaves; k++)
                {
                    sum += amp * noise.cnoise((p + offset) * f);
                    norm += amp;
                    amp *= 0.5f;
                    f *= 2f;
                }
                return math.clamp(sum / norm * contrast, -1f, 1f);
            }
        }
    }
}
