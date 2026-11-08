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
    /// 资源实体（GDD 14.4）：按（种类，部件）用 Graphics.RenderMeshInstanced 绘制。
    /// 组合形状（树、菌、岩堆、草丛）由多个基础网格拼成，部件定义在单位空间（底部 y = 0，高 1，宽 1），再按实体尺寸缩放。
    /// 只绘制与镜头视锥相交的 16 × 16 区块：
    /// - 有储量的实体是静态实例，只在该区块的 ChunkVersion 变化时重建；尺寸随剩余储量略微缩小。
    /// - 已取空的实体每帧按实时状态生成：正在再生的植物从幼苗（×0.3）长回完整尺寸，
    ///   用上次 1 Hz 结算的再生速度在两次结算之间插值；暂停再生的显示为灰褐色衰落外观。
    /// </summary>
    public sealed class ResourceRenderer : IDisposable
    {
        const int MaxBatch = 1023;

        struct Part
        {
            public Mesh Mesh;
            public Matrix4x4 Local;
            public bool Secondary;
        }

        readonly ResourceNodeDef[] _defs;
        readonly Part[][] _parts;               // [def][part]
        readonly Material[] _primary, _secondary;
        readonly Material _depletedMaterial;
        const byte MatPrimary = 0, MatSecondary = 1, MatDepleted = 2;
        const float SproutScale = 0.3f, DepletedScale = 0.55f;
        /// <summary>区块包围盒在水平方向外扩的 Tile 数（巨型树冠会伸出区块）。</summary>
        const float ChunkMargin = 6f;

        struct Inst
        {
            public int Slot;
            public byte Mat;
            public Matrix4x4 Matrix;
        }

        sealed class ChunkCache
        {
            public readonly List<Inst> Static = new List<Inst>();
            public readonly List<int> Dynamic = new List<int>(); // 已取空的实体索引，每帧按实时状态绘制
            public Bounds Bounds;
            public int Version = -1;
        }

        readonly int[][] _slotOf;              // [def][part] -> slot
        readonly int[] _slotDef, _slotPart;
        readonly List<Matrix4x4>[][] _draw;    // [slot][mat]
        readonly Plane[] _planes = new Plane[6];
        ChunkCache[] _chunks;

        public int VisibleChunks { get; private set; }
        public int DrawnInstances { get; private set; }

        static Mesh s_sphere, s_capsule, s_cylinder, s_cube;

        public ResourceRenderer(ResourceNodeDef[] defs, Material template)
        {
            _defs = defs;
            EnsureMeshes();
            _parts = new Part[defs.Length][];
            _primary = new Material[defs.Length];
            _secondary = new Material[defs.Length];
            _slotOf = new int[defs.Length][];
            var slotDef = new List<int>();
            var slotPart = new List<int>();
            for (int d = 0; d < defs.Length; d++)
            {
                _parts[d] = BuildParts(defs[d].shape);
                bool fadeable = defs[d].footprint > 1; // 巨型实体可悬停虚化
                _primary[d] = MakeMaterial(template, defs[d].color, defs[d].id, fadeable);
                _secondary[d] = MakeMaterial(template, defs[d].secondaryColor, defs[d].id + "_2", fadeable);
                _slotOf[d] = new int[_parts[d].Length];
                for (int p = 0; p < _parts[d].Length; p++)
                {
                    _slotOf[d][p] = slotDef.Count;
                    slotDef.Add(d);
                    slotPart.Add(p);
                }
            }
            _slotDef = slotDef.ToArray();
            _slotPart = slotPart.ToArray();
            _draw = new List<Matrix4x4>[_slotDef.Length][];
            for (int k = 0; k < _draw.Length; k++)
                _draw[k] = new[] { new List<Matrix4x4>(), new List<Matrix4x4>(), new List<Matrix4x4>() };
            _depletedMaterial = MakeMaterial(template, new Color(0.32f, 0.3f, 0.27f), "depleted", false);
        }

        // ---------------------------------------------------------------- shapes

        static void EnsureMeshes()
        {
            if (s_sphere != null) return;
            s_sphere = PrimitiveMesh(PrimitiveType.Sphere);
            s_capsule = PrimitiveMesh(PrimitiveType.Capsule);
            s_cylinder = PrimitiveMesh(PrimitiveType.Cylinder);
            s_cube = PrimitiveMesh(PrimitiveType.Cube);
        }

        static Mesh PrimitiveMesh(PrimitiveType type)
        {
            var go = GameObject.CreatePrimitive(type);
            var mesh = go.GetComponent<MeshFilter>().sharedMesh;
            Object.Destroy(go);
            return mesh;
        }

        /// <summary>单位空间部件：pos = 部件中心，scale = 部件包围盒尺寸（Capsule / Cylinder 网格高 2，已换算）。</summary>
        static Part P(Mesh mesh, Vector3 center, Vector3 size, Vector3 euler = default, bool secondary = false)
        {
            bool tall = mesh == s_capsule || mesh == s_cylinder;
            var scale = tall ? new Vector3(size.x, size.y * 0.5f, size.z) : size;
            return new Part { Mesh = mesh, Local = Matrix4x4.TRS(center, Quaternion.Euler(euler), scale), Secondary = secondary };
        }

        static Part[] BuildParts(NodeShape shape)
        {
            switch (shape)
            {
                case NodeShape.Capsule: return new[] { P(s_capsule, new Vector3(0, 0.5f, 0), Vector3.one) };
                case NodeShape.Cylinder: return new[] { P(s_cylinder, new Vector3(0, 0.5f, 0), Vector3.one) };
                case NodeShape.Cube: return new[] { P(s_cube, new Vector3(0, 0.5f, 0), Vector3.one) };
                // 树干宽 0.38、根部 0.5（相对整体宽度）：巨型树的 size.x ≈ 占地 / 0.38，使树根正好铺满占地
                case NodeShape.Tree:
                    return new[]
                    {
                        P(s_cylinder, new Vector3(0, 0.03f, 0), new Vector3(0.5f, 0.06f, 0.5f), default, true),
                        P(s_cylinder, new Vector3(0, 0.3f, 0), new Vector3(0.38f, 0.6f, 0.38f), default, true),
                        P(s_sphere, new Vector3(0, 0.64f, 0), new Vector3(1f, 0.38f, 1f)),
                        P(s_sphere, new Vector3(0.1f, 0.82f, -0.06f), new Vector3(0.72f, 0.3f, 0.72f)),
                        P(s_sphere, new Vector3(-0.05f, 0.95f, 0.05f), new Vector3(0.4f, 0.2f, 0.4f)),
                    };
                case NodeShape.Conifer:
                    return new[]
                    {
                        P(s_cylinder, new Vector3(0, 0.03f, 0), new Vector3(0.5f, 0.06f, 0.5f), default, true),
                        P(s_cylinder, new Vector3(0, 0.35f, 0), new Vector3(0.38f, 0.7f, 0.38f), default, true),
                        P(s_sphere, new Vector3(0, 0.32f, 0), new Vector3(1f, 0.2f, 1f)),
                        P(s_sphere, new Vector3(0, 0.5f, 0), new Vector3(0.8f, 0.18f, 0.8f)),
                        P(s_sphere, new Vector3(0, 0.66f, 0), new Vector3(0.6f, 0.16f, 0.6f)),
                        P(s_sphere, new Vector3(0, 0.8f, 0), new Vector3(0.4f, 0.14f, 0.4f)),
                        P(s_capsule, new Vector3(0, 0.92f, 0), new Vector3(0.18f, 0.16f, 0.18f)),
                    };
                // 菌柄宽 0.36（巨菌 size.x ≈ 占地 / 0.36）
                case NodeShape.Mushroom:
                    return new[]
                    {
                        P(s_cylinder, new Vector3(0, 0.03f, 0), new Vector3(0.46f, 0.06f, 0.46f), default, true),
                        P(s_cylinder, new Vector3(0, 0.3f, 0), new Vector3(0.36f, 0.6f, 0.36f), default, true),
                        P(s_sphere, new Vector3(0, 0.7f, 0), new Vector3(1f, 0.45f, 1f)),
                    };
                case NodeShape.Rocks:
                    return new[]
                    {
                        P(s_cube, new Vector3(0f, 0.32f, 0f), new Vector3(0.62f, 0.64f, 0.56f), new Vector3(8, 20, 6)),
                        P(s_cube, new Vector3(0.3f, 0.22f, 0.22f), new Vector3(0.42f, 0.44f, 0.4f), new Vector3(-10, 52, 14)),
                        P(s_cube, new Vector3(-0.26f, 0.4f, -0.18f), new Vector3(0.36f, 0.8f, 0.34f), new Vector3(6, -28, -12)),
                    };
                case NodeShape.Tuft:
                    return new[]
                    {
                        P(s_capsule, new Vector3(0f, 0.5f, 0f), new Vector3(0.22f, 1f, 0.22f)),
                        P(s_capsule, new Vector3(0.22f, 0.4f, 0.1f), new Vector3(0.2f, 0.82f, 0.2f), new Vector3(0, 0, -14)),
                        P(s_capsule, new Vector3(-0.2f, 0.38f, 0.14f), new Vector3(0.2f, 0.78f, 0.2f), new Vector3(12, 0, 12)),
                        P(s_capsule, new Vector3(0.05f, 0.34f, -0.22f), new Vector3(0.18f, 0.7f, 0.18f), new Vector3(-14, 0, 4)),
                    };
                default: return new[] { P(s_sphere, new Vector3(0, 0.5f, 0), Vector3.one) };
            }
        }

        static Material MakeMaterial(Material template, Color color, string name, bool fadeable)
        {
            var m = new Material(template) { name = $"Resource_{name}", enableInstancing = true };
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
            if (m.HasProperty("_Fadeable")) m.SetFloat("_Fadeable", fadeable ? 1f : 0f);
            m.color = color;
            return m;
        }

        // ---------------------------------------------------------------- drawing

        /// <param name="cam">用于视锥剔除；为空时绘制全部区块。</param>
        /// <param name="simTime">模拟时间（秒），与 ResourceField.Time 之差用于插值再生进度。</param>
        public void Draw(ResourceField field, EnvGrid env, GameBalance b, Camera cam, double simTime)
        {
            if (field == null || !field.Nodes.IsCreated) return;
            int chunkCount = field.ChunkRanges.Length;
            if (_chunks == null || _chunks.Length != chunkCount) AllocChunks(field, b);

            for (int k = 0; k < _draw.Length; k++)
            for (int m = 0; m < 3; m++)
                _draw[k][m].Clear();

            bool cull = cam != null;
            if (cull) GeometryUtility.CalculateFrustumPlanes(cam, _planes);
            float since = (float)math.clamp(simTime - field.Time, 0.0, 1.0);

            int visible = 0, drawn = 0;
            for (int c = 0; c < chunkCount; c++)
            {
                var chunk = _chunks[c];
                if (cull && !GeometryUtility.TestPlanesAABB(_planes, chunk.Bounds)) continue;
                visible++;
                if (chunk.Version != field.ChunkVersion(c)) RebuildChunk(c, field, env, b);

                foreach (var inst in chunk.Static) _draw[inst.Slot][inst.Mat].Add(inst.Matrix);
                drawn += chunk.Static.Count;
                foreach (int n in chunk.Dynamic) drawn += AddDynamic(field, n, env, b, since);
            }
            VisibleChunks = visible;
            DrawnInstances = drawn;

            for (int k = 0; k < _draw.Length; k++)
            {
                int d = _slotDef[k];
                var mesh = _parts[d][_slotPart[k]].Mesh;
                DrawList(mesh, _primary[d], _draw[k][MatPrimary]);
                DrawList(mesh, _secondary[d], _draw[k][MatSecondary]);
                DrawList(mesh, _depletedMaterial, _draw[k][MatDepleted]);
            }
        }

        static void DrawList(Mesh mesh, Material mat, List<Matrix4x4> list)
        {
            if (list.Count == 0) return;
            var rp = new RenderParams(mat) { shadowCastingMode = ShadowCastingMode.On, receiveShadows = true };
            for (int start = 0; start < list.Count; start += MaxBatch)
                Graphics.RenderMeshInstanced(rp, mesh, 0, list, math.min(MaxBatch, list.Count - start), start);
        }

        void AllocChunks(ResourceField field, GameBalance b)
        {
            int n = field.ChunkRanges.Length, side = field.ChunksPerSide, cs = field.ChunkSize;
            float top = b.cliffHeight * (b.mountainVisibleLevels + 1f);
            foreach (var def in _defs) top = math.max(top, def.size.y * def.sizeJitter.y + b.cliffHeight * 2f);
            _chunks = new ChunkCache[n];
            for (int c = 0; c < n; c++)
            {
                float x0 = (c % side) * cs - ChunkMargin, y0 = (c / side) * cs - ChunkMargin;
                float w = cs + ChunkMargin * 2f;
                var bounds = new Bounds();
                bounds.SetMinMax(new Vector3(x0, -2f, y0), new Vector3(x0 + w, top, y0 + w));
                _chunks[c] = new ChunkCache { Bounds = bounds };
            }
        }

        /// <summary>重建一个区块：有储量的实体写成静态实例，已取空的记入动态列表。</summary>
        void RebuildChunk(int c, ResourceField field, EnvGrid env, GameBalance b)
        {
            var chunk = _chunks[c];
            chunk.Version = field.ChunkVersion(c);
            chunk.Static.Clear();
            chunk.Dynamic.Clear();
            int2 range = field.ChunkRanges[c];
            for (int n = range.x; n < range.x + range.y; n++)
            {
                var node = field.Nodes[n];
                if (node.Alive == 0) continue;
                bool full = node.Infinite != 0 || node.Amount > 0f;
                if (!full) { chunk.Dynamic.Add(n); continue; }
                float frac = node.Infinite != 0 || node.Capacity <= 0f ? 1f : math.saturate(node.Amount / node.Capacity);
                var root = RootMatrix(n, node, env, b, 0.6f + 0.4f * frac);
                var parts = _parts[node.Def];
                for (int p = 0; p < parts.Length; p++)
                    chunk.Static.Add(new Inst
                    {
                        Slot = _slotOf[node.Def][p],
                        Mat = parts[p].Secondary ? MatSecondary : MatPrimary,
                        Matrix = root * parts[p].Local,
                    });
            }
        }

        /// <summary>已取空实体的当前外观：再生中 → 幼苗长回原尺寸；暂停 → 衰落。返回实例数。</summary>
        int AddDynamic(ResourceField field, int n, EnvGrid env, GameBalance b, float since)
        {
            var node = field.Nodes[n];
            if (node.Alive == 0) return 0;
            var def = _defs[node.Def];
            bool growing = node.RegrowState == 1 && def.regrowSeconds > 0f;
            float scale = DepletedScale;
            if (growing)
            {
                float progress = math.saturate((node.RegrowTimer + node.RegrowRate * since) / def.regrowSeconds);
                scale = math.lerp(SproutScale, 1f, progress);
            }

            var root = RootMatrix(n, node, env, b, scale);
            var parts = _parts[node.Def];
            for (int p = 0; p < parts.Length; p++)
            {
                byte mat = growing ? (parts[p].Secondary ? MatSecondary : MatPrimary) : MatDepleted;
                _draw[_slotOf[node.Def][p]][mat].Add(root * parts[p].Local);
            }
            return parts.Length;
        }

        Matrix4x4 RootMatrix(int n, ResourceNode node, EnvGrid env, GameBalance b, float scale)
        {
            var def = _defs[node.Def];
            uint hsh = math.hash(new int2(n, node.Def));
            float yaw = hsh % 360u;
            float s = (node.Scale > 0f ? node.Scale : 1f) * scale;
            float ground = node.Footprint > 1
                ? MinGround(env, b, node.Position, node.Footprint)
                : TerrainHeights.Sample(env, b, node.Position);
            return Matrix4x4.TRS(new Vector3(node.Position.x, ground, node.Position.y), Quaternion.Euler(0f, yaw, 0f), def.size * s);
        }

        /// <summary>巨型实体取占地范围内的最低地面，避免悬空。</summary>
        static float MinGround(EnvGrid env, GameBalance b, float2 center, int footprint)
        {
            float half = footprint * 0.5f - 0.1f;
            float h = TerrainHeights.Sample(env, b, center);
            h = math.min(h, TerrainHeights.Sample(env, b, center + new float2(-half, -half)));
            h = math.min(h, TerrainHeights.Sample(env, b, center + new float2(half, -half)));
            h = math.min(h, TerrainHeights.Sample(env, b, center + new float2(half, half)));
            h = math.min(h, TerrainHeights.Sample(env, b, center + new float2(-half, half)));
            return h;
        }

        public void Dispose()
        {
            foreach (var m in _primary) if (m != null) Object.Destroy(m);
            foreach (var m in _secondary) if (m != null) Object.Destroy(m);
            if (_depletedMaterial != null) Object.Destroy(_depletedMaterial);
        }
    }
}
