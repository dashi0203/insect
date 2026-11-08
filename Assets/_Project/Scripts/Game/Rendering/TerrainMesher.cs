using System;
using System.Collections.Generic;
using Swarm.Data;
using Swarm.World;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Swarm.Rendering
{
    /// <summary>
    /// 地形分块网格（GDD 14.4）：32 × 32 Tile 一块，顶点色 = 群系颜色按权重混合，悬崖挤出侧壁，坡道为斜面。
    /// 高山：平顶块 + 直上直下的侧壁，侧壁按高度切成岩层条带（蛋糕横截面）；
    /// 可见高度最多 mountainVisibleLevels 层悬崖，真实高度超过的台面涂成黑影。
    /// 顶点色 alpha = 可积雪（水面、侧壁、高山为 0）；UV0.x = 高山标记（Shader 据此做鼠标悬停虚化）。
    /// </summary>
    /// <summary>地表调试着色：营养土肥力（棕 → 绿，超过 100 → 金黄）、土壤含水量 / 干旱图（沙黄 → 蓝）。</summary>
    public enum TerrainOverlay { None, Fertility, Moisture }

    public sealed class TerrainMesher : IDisposable
    {
        public TerrainOverlay Overlay { get; set; }

        readonly Transform _root;
        readonly Material _material;
        readonly List<GameObject> _chunks = new List<GameObject>();
        readonly List<Mesh> _meshes = new List<Mesh>();

        readonly List<Vector3> _v = new List<Vector3>();
        readonly List<Color> _c = new List<Color>();
        readonly List<Vector2> _uv = new List<Vector2>();
        readonly List<int> _t = new List<int>();

        static readonly Color WaterColor = new Color(0.13f, 0.27f, 0.34f, 0f);
        static readonly Color RockLow = new Color(0.32f, 0.29f, 0.26f, 0f);
        static readonly Color RockHigh = new Color(0.5f, 0.48f, 0.45f, 0f);
        static readonly Color Shadow = new Color(0.02f, 0.02f, 0.025f, 0f);
        const float SkirtDepth = 3f;
        GameBalance _balance;

        public TerrainMesher(Transform parent, Material material)
        {
            _root = new GameObject("Terrain").transform;
            _root.SetParent(parent, false);
            _material = material;
        }

        public void BuildAll(EnvGrid env, GameBalance b, RegionDef region)
        {
            Clear();
            int cs = math.max(1, b.terrainChunkSize);
            int per = (env.Size + cs - 1) / cs;
            for (int cy = 0; cy < per; cy++)
            for (int cx = 0; cx < per; cx++)
            {
                var mesh = BuildChunk(env, b, region, cx * cs, cy * cs, cs);
                mesh.name = $"TerrainChunk_{cx}_{cy}";
                var go = new GameObject(mesh.name);
                go.transform.SetParent(_root, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = _material;
                mr.shadowCastingMode = ShadowCastingMode.On;
                mr.receiveShadows = true;
                _chunks.Add(go);
                _meshes.Add(mesh);
            }
        }

        Mesh BuildChunk(EnvGrid env, GameBalance b, RegionDef region, int x0, int y0, int cs)
        {
            _v.Clear(); _c.Clear(); _uv.Clear(); _t.Clear();
            _balance = b;
            int x1 = math.min(env.Size, x0 + cs), y1 = math.min(env.Size, y0 + cs);

            for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int i = env.Index(x, y);
                bool mountain = env.IsObstacle(i);
                float4 h = TerrainHeights.Corners(env, b, x, y);
                Color top;
                if (mountain)
                {
                    top = MountainTopColor(env, b, h.x, x, y);
                    AddTop(x, y, h, top, top, top, top, 1f);
                }
                else
                {
                    top = TileColor(env, region, i, x, y);
                    AddTop(x, y, h, top, top, top, top, 0f);
                }

                Color wall = mountain ? MountainWallBase(x, y) : top * 0.55f;
                wall.a = 0f;
                float myFlag = mountain ? 1f : 0f;

                // 东边与北边由本 Tile 负责；地图边缘再补一圈裙边
                if (x + 1 < env.Size)
                {
                    float4 n = TerrainHeights.Corners(env, b, x + 1, y);
                    EdgeWalls(new float3(x + 1, 0, y), new float3(x + 1, 0, y + 1), h.y, h.z, n.x, n.w, true,
                        wall, myFlag, WallColor(env, region, x + 1, y), Flag(env, x + 1, y));
                }
                else AddWall(new float3(x + 1, 0, y), new float3(x + 1, 0, y + 1), h.y, h.z, -SkirtDepth, -SkirtDepth, true, wall, myFlag);

                if (y + 1 < env.Size)
                {
                    float4 n = TerrainHeights.Corners(env, b, x, y + 1);
                    EdgeWalls(new float3(x, 0, y + 1), new float3(x + 1, 0, y + 1), h.w, h.z, n.x, n.y, false,
                        wall, myFlag, WallColor(env, region, x, y + 1), Flag(env, x, y + 1));
                }
                else AddWall(new float3(x, 0, y + 1), new float3(x + 1, 0, y + 1), h.w, h.z, -SkirtDepth, -SkirtDepth, false, wall, myFlag);

                if (x == 0)
                    AddWall(new float3(0, 0, y), new float3(0, 0, y + 1), h.x, h.w, -SkirtDepth, -SkirtDepth, false, wall, myFlag);
                if (y == 0)
                    AddWall(new float3(x, 0, 0), new float3(x + 1, 0, 0), h.x, h.y, -SkirtDepth, -SkirtDepth, true, wall, myFlag);
            }

            var mesh = new Mesh { indexFormat = IndexFormat.UInt32 };
            mesh.SetVertices(_v);
            mesh.SetColors(_c);
            mesh.SetUVs(0, _uv);
            mesh.SetTriangles(_t, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        static float Flag(EnvGrid env, int x, int y) => env.IsObstacle(env.Index(x, y)) ? 1f : 0f;

        Color TileColor(EnvGrid env, RegionDef region, int i, int x, int y)
        {
            if (env.IsWater(i)) return WaterColor;
            if (Overlay == TerrainOverlay.Fertility)
            {
                float f = env.Fertility[i];
                return f <= 1f
                    ? Color.Lerp(new Color(0.32f, 0.22f, 0.12f, 1f), new Color(0.2f, 0.62f, 0.16f, 1f), f)
                    : Color.Lerp(new Color(0.2f, 0.62f, 0.16f, 1f), new Color(1f, 0.82f, 0.15f, 1f), math.saturate((f - 1f) * 2f));
            }
            if (Overlay == TerrainOverlay.Moisture)
                return Color.Lerp(new Color(0.88f, 0.76f, 0.42f, 1f), new Color(0.14f, 0.34f, 0.72f, 1f), env.SoilMoisture[i]);
            Color c = Biomes.BlendColor(region, env.BiomeWeights[i]);
            uint hsh = math.hash(new int2(x, y));
            float jitter = 0.94f + 0.12f * ((hsh & 0xFFFF) / 65535f);
            c *= jitter;
            if (env.IsCliff(i)) c *= 1.08f;
            c.a = 1f;
            return c;
        }

        Color WallColor(EnvGrid env, RegionDef region, int x, int y)
        {
            int i = env.Index(x, y);
            var c = env.IsObstacle(i) ? MountainWallBase(x, y) : TileColor(env, region, i, x, y) * 0.55f;
            c.a = 0f;
            return c;
        }

        /// <summary>
        /// 高山台面颜色：低处深、高处浅，真实高度超过可见上限的台面渐变为黑影。
        /// </summary>
        static Color MountainTopColor(EnvGrid env, GameBalance b, float visibleHeight, int x, int y)
        {
            float cap = TerrainHeights.MountainVisibleCap(b);
            float trueHeight = TerrainHeights.MountainTrueHeight(env, b, x, y);
            float t = math.saturate(visibleHeight / math.max(0.01f, cap));
            uint hsh = math.hash(new int2(x * 7 + 3, y * 13 + 5));
            float jitter = 0.92f + 0.16f * ((hsh & 0xFFFF) / 65535f);
            Color rock = Color.Lerp(RockLow, RockHigh, t) * jitter * 1.1f;
            float shadow = math.smoothstep(cap + 0.01f, cap + 2.5f, trueHeight);
            Color c = Color.Lerp(rock, Shadow, shadow);
            c.a = 0f;
            return c;
        }

        /// <summary>高山侧壁的基色（每个 Tile 略有差异）。</summary>
        static Color MountainWallBase(int x, int y)
        {
            uint hsh = math.hash(new int2(x * 5 + 11, y * 3 + 2));
            float j = 0.9f + 0.2f * ((hsh & 0xFFFF) / 65535f);
            Color c = RockLow * 0.85f * j;
            c.a = 0f;
            return c;
        }

        /// <summary>
        /// 岩层条带颜色：按世界高度分层（同一高度的条带在整座山上连续），深浅、冷暖交替——蛋糕横截面。
        /// </summary>
        static Color StrataColor(Color baseColor, int band)
        {
            uint hsh = math.hash(new int2(band, 977));
            float r = (hsh & 0xFFFF) / 65535f;
            float shade = (band & 1) == 0 ? 1.12f : 0.78f;
            shade *= 0.92f + 0.16f * r;
            var warm = new Color(1.06f, 1f, 0.92f);
            var cool = new Color(0.94f, 0.98f, 1.06f);
            Color tint = ((hsh >> 16) & 1u) == 0 ? warm : cool;
            Color c = baseColor * shade * tint;
            c.a = 0f;
            return c;
        }

        void AddTop(int x, int y, float4 h, Color c0, Color c1, Color c2, Color c3, float flag)
        {
            int s = _v.Count;
            _v.Add(new Vector3(x, h.x, y));
            _v.Add(new Vector3(x + 1, h.y, y));
            _v.Add(new Vector3(x + 1, h.z, y + 1));
            _v.Add(new Vector3(x, h.w, y + 1));
            _c.Add(c0); _c.Add(c1); _c.Add(c2); _c.Add(c3);
            var uv = new Vector2(flag, 0f);
            _uv.Add(uv); _uv.Add(uv); _uv.Add(uv); _uv.Add(uv);
            _t.Add(s); _t.Add(s + 3); _t.Add(s + 2);
            _t.Add(s); _t.Add(s + 2); _t.Add(s + 1);
        }

        /// <summary>
        /// 共享边两侧高度不同时补侧壁。p0/p1 为边的两端；a0/a1 为本 Tile 在两端的高度，b0/b1 为邻居的。
        /// alongZ = 边沿 z 方向（东边），否则沿 x 方向（北边）。
        /// </summary>
        void EdgeWalls(float3 p0, float3 p1, float a0, float a1, float b0, float b1, bool alongZ,
            Color mine, float mineFlag, Color theirs, float theirFlag)
        {
            if (a0 > b0 + 1e-4f || a1 > b1 + 1e-4f)
                AddWall(p0, p1, a0, a1, math.min(a0, b0), math.min(a1, b1), alongZ, mine, mineFlag);
            if (b0 > a0 + 1e-4f || b1 > a1 + 1e-4f)
                AddWall(p0, p1, b0, b1, math.min(a0, b0), math.min(a1, b1), !alongZ, theirs, theirFlag);
        }

        /// <summary>
        /// 侧壁四边形（p0→p1 为边，底高 bot、顶高 top）。
        /// 沿 z 的边：flip = true 朝 +x，false 朝 −x；沿 x 的边：flip = true 朝 −z，false 朝 +z。
        /// </summary>
        void AddWall(float3 p0, float3 p1, float top0, float top1, float bot0, float bot1, bool flip, Color color, float flag)
        {
            float band = _balance != null ? _balance.mountainStrataBand : 0f;
            if (flag > 0.5f && band > 0.05f)
            {
                // 高山侧壁：按世界高度切成水平条带
                float lo = math.min(bot0, bot1), hi = math.max(top0, top1);
                int k0 = (int)math.floor(lo / band), k1 = (int)math.ceil(hi / band);
                for (int k = k0; k < k1; k++)
                {
                    float y0 = k * band, y1 = (k + 1) * band;
                    float t0 = math.clamp(top0, y0, y1), t1 = math.clamp(top1, y0, y1);
                    float b0 = math.clamp(bot0, y0, y1), b1 = math.clamp(bot1, y0, y1);
                    if (t0 - b0 < 1e-4f && t1 - b1 < 1e-4f) continue;
                    AddWallQuad(p0, p1, t0, t1, b0, b1, flip, StrataColor(color, k), flag);
                }
                return;
            }
            AddWallQuad(p0, p1, top0, top1, bot0, bot1, flip, color, flag);
        }

        void AddWallQuad(float3 p0, float3 p1, float top0, float top1, float bot0, float bot1, bool flip, Color color, float flag)
        {
            int s = _v.Count;
            _v.Add(new Vector3(p0.x, bot0, p0.z));
            _v.Add(new Vector3(p0.x, top0, p0.z));
            _v.Add(new Vector3(p1.x, top1, p1.z));
            _v.Add(new Vector3(p1.x, bot1, p1.z));
            for (int k = 0; k < 4; k++) _c.Add(color);
            var uv = new Vector2(flag, 0f);
            _uv.Add(uv); _uv.Add(uv); _uv.Add(uv); _uv.Add(uv);
            if (flip)
            {
                _t.Add(s); _t.Add(s + 1); _t.Add(s + 2);
                _t.Add(s); _t.Add(s + 2); _t.Add(s + 3);
            }
            else
            {
                _t.Add(s); _t.Add(s + 2); _t.Add(s + 1);
                _t.Add(s); _t.Add(s + 3); _t.Add(s + 2);
            }
        }

        public void Clear()
        {
            foreach (var go in _chunks) if (go != null) Object.Destroy(go);
            foreach (var m in _meshes) if (m != null) Object.Destroy(m);
            _chunks.Clear();
            _meshes.Clear();
        }

        public void Dispose()
        {
            Clear();
            if (_root != null) Object.Destroy(_root.gameObject);
        }
    }
}
