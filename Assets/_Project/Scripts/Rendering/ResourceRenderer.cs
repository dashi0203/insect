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
    /// 储量为 0 时整体缩小并换成灰褐色“衰落”材质。实例矩阵只在 ResourceField.Version 变化时重建。
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
        readonly List<Matrix4x4>[][] _full, _depleted; // [def][part]
        int _version = -1;

        static Mesh s_sphere, s_capsule, s_cylinder, s_cube;

        public ResourceRenderer(ResourceNodeDef[] defs, Material template)
        {
            _defs = defs;
            EnsureMeshes();
            _parts = new Part[defs.Length][];
            _primary = new Material[defs.Length];
            _secondary = new Material[defs.Length];
            _full = new List<Matrix4x4>[defs.Length][];
            _depleted = new List<Matrix4x4>[defs.Length][];
            for (int d = 0; d < defs.Length; d++)
            {
                _parts[d] = BuildParts(defs[d].shape);
                bool fadeable = defs[d].footprint > 1; // 巨型实体可悬停虚化
                _primary[d] = MakeMaterial(template, defs[d].color, defs[d].id, fadeable);
                _secondary[d] = MakeMaterial(template, defs[d].secondaryColor, defs[d].id + "_2", fadeable);
                _full[d] = new List<Matrix4x4>[_parts[d].Length];
                _depleted[d] = new List<Matrix4x4>[_parts[d].Length];
                for (int p = 0; p < _parts[d].Length; p++)
                {
                    _full[d][p] = new List<Matrix4x4>();
                    _depleted[d][p] = new List<Matrix4x4>();
                }
            }
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

        public void Draw(ResourceField field, EnvGrid env, GameBalance b)
        {
            if (field == null || !field.Nodes.IsCreated) return;
            if (field.Version != _version) Rebuild(field, env, b);

            for (int d = 0; d < _defs.Length; d++)
            for (int p = 0; p < _parts[d].Length; p++)
            {
                var part = _parts[d][p];
                DrawList(part.Mesh, part.Secondary ? _secondary[d] : _primary[d], _full[d][p]);
                DrawList(part.Mesh, _depletedMaterial, _depleted[d][p]);
            }
        }

        static void DrawList(Mesh mesh, Material mat, List<Matrix4x4> list)
        {
            if (list.Count == 0) return;
            var rp = new RenderParams(mat) { shadowCastingMode = ShadowCastingMode.On, receiveShadows = true };
            for (int start = 0; start < list.Count; start += MaxBatch)
                Graphics.RenderMeshInstanced(rp, mesh, 0, list, math.min(MaxBatch, list.Count - start), start);
        }

        void Rebuild(ResourceField field, EnvGrid env, GameBalance b)
        {
            _version = field.Version;
            for (int d = 0; d < _defs.Length; d++)
            for (int p = 0; p < _parts[d].Length; p++)
            {
                _full[d][p].Clear();
                _depleted[d][p].Clear();
            }

            for (int n = 0; n < field.Nodes.Length; n++)
            {
                var node = field.Nodes[n];
                if (node.Alive == 0) continue;
                var def = _defs[node.Def];
                uint hsh = math.hash(new int2(n, node.Def));
                float yaw = hsh % 360u;
                bool full = node.Infinite != 0 || node.Amount > 0f;
                float s = (node.Scale > 0f ? node.Scale : 1f) * (full ? 1f : 0.55f);
                float ground = node.Footprint > 1
                    ? MinGround(env, b, node.Position, node.Footprint)
                    : TerrainHeights.Sample(env, b, node.Position);
                var root = Matrix4x4.TRS(new Vector3(node.Position.x, ground, node.Position.y),
                    Quaternion.Euler(0f, yaw, 0f), def.size * s);

                var lists = full ? _full[node.Def] : _depleted[node.Def];
                var parts = _parts[node.Def];
                for (int p = 0; p < parts.Length; p++) lists[p].Add(root * parts[p].Local);
            }
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
