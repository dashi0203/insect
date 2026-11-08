using System;
using Swarm.Agents;
using Swarm.Data;
using Swarm.World;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Swarm.Rendering
{
    /// <summary>
    /// 单位实例化渲染（GDD 3.3 / 14.4）：一个 Burst Job 对全部单位做视锥剔除、
    /// 按上一步 → 当前步插值位置、按体型缩放，写出身体 / 头 / 携带物三组矩阵，再用 RenderMeshInstanced 绘制。
    /// 不创建任何 GameObject；每帧零托管分配。
    /// </summary>
    public sealed class AgentRenderer : IDisposable
    {
        const int MaxBatch = 1023;
        const int Groups = 5; // 0 身体，1 头，2–4 携带物（蛋白质 / 矿物质 / 建筑质）

        readonly int _size;
        NativeArray<float4> _corners;  // 每格四角高度（与地形网格一致）
        NativeArray<float> _scales;    // 各体型的外观缩放
        NativeArray<Matrix4x4>[] _matrices = new NativeArray<Matrix4x4>[Groups];
        NativeArray<int> _counts;
        NativeArray<float4> _planes;
        readonly Plane[] _planeBuf = new Plane[6];
        readonly Material[] _materials = new Material[Groups];
        readonly Mesh _sphere, _cube;
        int _capacity;

        public int Drawn { get; private set; }

        public AgentRenderer(EnvGrid env, GameBalance b, Material template)
        {
            _size = env.Size;
            _corners = new NativeArray<float4>(env.Length, Allocator.Persistent);
            for (int y = 0; y < env.Size; y++)
            for (int x = 0; x < env.Size; x++)
                _corners[env.Index(x, y)] = TerrainHeights.Corners(env, b, x, y);

            var tiers = b.sizeTiers != null && b.sizeTiers.Length == 4 ? b.sizeTiers : SizeTierSettings.Defaults();
            _scales = new NativeArray<float>(4, Allocator.Persistent);
            for (int k = 0; k < 4; k++) _scales[k] = tiers[k].scale;
            _counts = new NativeArray<int>(Groups, Allocator.Persistent);
            _planes = new NativeArray<float4>(6, Allocator.Persistent);

            _sphere = Primitive(PrimitiveType.Sphere);
            _cube = Primitive(PrimitiveType.Cube);
            _materials[0] = Mat(template, new Color(0.16f, 0.11f, 0.07f), "AgentBody");
            _materials[1] = Mat(template, new Color(0.24f, 0.16f, 0.1f), "AgentHead");
            _materials[2] = Mat(template, new Color(1f, 0.45f, 0.7f), "CarryProtein");
            _materials[3] = Mat(template, new Color(0.3f, 0.85f, 1f), "CarryMineral");
            _materials[4] = Mat(template, new Color(0.8f, 0.6f, 0.35f), "CarryBuild");
        }

        static Mesh Primitive(PrimitiveType t)
        {
            var go = GameObject.CreatePrimitive(t);
            var mesh = go.GetComponent<MeshFilter>().sharedMesh;
            Object.Destroy(go);
            return mesh;
        }

        static Material Mat(Material template, Color c, string name)
        {
            var m = new Material(template) { name = name, enableInstancing = true };
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            if (m.HasProperty("_Fadeable")) m.SetFloat("_Fadeable", 0f);
            m.color = c;
            return m;
        }

        void EnsureCapacity(int n)
        {
            if (n <= _capacity) return;
            _capacity = math.max(1024, math.ceilpow2(n));
            for (int g = 0; g < Groups; g++)
            {
                if (_matrices[g].IsCreated) _matrices[g].Dispose();
                _matrices[g] = new NativeArray<Matrix4x4>(_capacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }
        }

        /// <param name="alpha">0 = 上一步，1 = 当前步（GameLoop.RenderAlpha）。</param>
        public void Draw(AgentStore store, float alpha, Camera cam)
        {
            if (store == null || store.Count == 0 || cam == null) { Drawn = 0; return; }
            EnsureCapacity(store.Count);
            GeometryUtility.CalculateFrustumPlanes(cam, _planeBuf);
            for (int k = 0; k < 6; k++)
                _planes[k] = new float4(_planeBuf[k].normal, _planeBuf[k].distance);

            new BuildJob
            {
                size = _size,
                alpha = math.saturate(alpha),
                agents = store.Agents.AsArray(),
                corners = _corners,
                scales = _scales,
                planes = _planes,
                body = _matrices[0],
                head = _matrices[1],
                carryProtein = _matrices[2],
                carryMineral = _matrices[3],
                carryBuild = _matrices[4],
                counts = _counts,
            }.Run();

            Drawn = _counts[0];
            for (int g = 0; g < Groups; g++)
            {
                int count = _counts[g];
                if (count == 0) continue;
                var rp = new RenderParams(_materials[g]) { shadowCastingMode = ShadowCastingMode.Off, receiveShadows = true };
                var mesh = g >= 2 ? _cube : _sphere;
                for (int start = 0; start < count; start += MaxBatch)
                    Graphics.RenderMeshInstanced(rp, mesh, 0, _matrices[g], math.min(MaxBatch, count - start), start);
            }
        }

        public void Dispose()
        {
            if (_corners.IsCreated) _corners.Dispose();
            if (_scales.IsCreated) _scales.Dispose();
            if (_counts.IsCreated) _counts.Dispose();
            if (_planes.IsCreated) _planes.Dispose();
            for (int g = 0; g < Groups; g++) if (_matrices[g].IsCreated) _matrices[g].Dispose();
            foreach (var m in _materials) if (m != null) Object.Destroy(m);
        }

        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Fast)]
        struct BuildJob : IJob
        {
            public int size;
            public float alpha;
            [ReadOnly] public NativeArray<AgentData> agents;
            [ReadOnly] public NativeArray<float4> corners;
            [ReadOnly] public NativeArray<float> scales;
            [ReadOnly] public NativeArray<float4> planes;
            [WriteOnly] public NativeArray<Matrix4x4> body, head, carryProtein, carryMineral, carryBuild;
            public NativeArray<int> counts;

            public void Execute()
            {
                int nb = 0, np = 0, nm = 0, nc = 0;
                for (int i = 0; i < agents.Length; i++)
                {
                    var a = agents[i];
                    if (!a.IsAlive || a.Layer != 0) continue;
                    float2 p2 = math.lerp(a.PrevPosition, a.Position, alpha);
                    float s = scales[a.Tier];
                    float3 p = new float3(p2.x, Height(p2), p2.y);
                    if (!Visible(p, s)) continue;

                    float2 f2 = a.Position - a.PrevPosition;
                    if (math.lengthsq(f2) < 1e-8f) f2 = new float2(math.cos(a.WanderAngle), math.sin(a.WanderAngle));
                    f2 = math.normalize(f2);
                    float3 fwd = new float3(f2.x, 0f, f2.y);
                    quaternion rot = quaternion.LookRotationSafe(fwd, math.up());

                    body[nb] = float4x4.TRS(p + new float3(0, 0.16f * s, 0), rot, new float3(0.3f, 0.2f, 0.62f) * s);
                    head[nb] = float4x4.TRS(p + fwd * (0.36f * s) + new float3(0, 0.19f * s, 0), rot, new float3(0.22f) * s);
                    nb++;

                    if (a.Carry > 0f)
                    {
                        var m = (Matrix4x4)float4x4.TRS(p + fwd * (0.5f * s) + new float3(0, 0.2f * s, 0), rot, new float3(0.2f) * s);
                        if (a.CarryKind == 0) carryProtein[np++] = m;
                        else if (a.CarryKind == 1) carryMineral[nm++] = m;
                        else carryBuild[nc++] = m;
                    }
                }
                counts[0] = nb; counts[1] = nb; counts[2] = np; counts[3] = nm; counts[4] = nc;
            }

            bool Visible(float3 p, float r)
            {
                for (int k = 0; k < 6; k++)
                    if (math.dot(planes[k].xyz, p) + planes[k].w < -r) return false;
                return true;
            }

            float Height(float2 q)
            {
                int x = math.clamp((int)math.floor(q.x), 0, size - 1), y = math.clamp((int)math.floor(q.y), 0, size - 1);
                float4 c = corners[y * size + x];
                float fx = math.saturate(q.x - x), fy = math.saturate(q.y - y);
                return math.lerp(math.lerp(c.x, c.y, fx), math.lerp(c.w, c.z, fx), fy);
            }
        }
    }
}
