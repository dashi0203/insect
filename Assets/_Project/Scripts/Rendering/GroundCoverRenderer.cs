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
    /// 地被层（GDD 2.7 / 14.4）：地表上方一层薄网格，覆盖度 ≥ 阈值的 Tile 按 47 格规则选瓦片，覆盖度控制透明度。
    /// 只重建 GroundCoverField.DirtyChunks 标记的分块。
    /// </summary>
    public sealed class GroundCoverRenderer : IDisposable
    {
        const float Lift = 0.04f;

        readonly Transform _root;
        readonly Material _material;
        readonly Texture2D _atlas;
        GameObject[] _chunks = Array.Empty<GameObject>();
        Mesh[] _meshes = Array.Empty<Mesh>();

        readonly List<Vector3> _v = new List<Vector3>();
        readonly List<Color> _c = new List<Color>();
        readonly List<Vector2> _uv = new List<Vector2>();
        readonly List<int> _t = new List<int>();

        public GroundCoverRenderer(Transform parent, Material material)
        {
            _root = new GameObject("GroundCover").transform;
            _root.SetParent(parent, false);
            _atlas = BlobTiles.BuildAtlas();
            _material = new Material(material) { name = "GroundCover (runtime)" };
            _material.mainTexture = _atlas;
            if (_material.HasProperty("_MainTex")) _material.SetTexture("_MainTex", _atlas);
        }

        public void Rebuild(EnvGrid env, GameBalance b, GroundCoverField field, bool all)
        {
            int per = field.ChunksPerSide;
            if (_chunks.Length != per * per) CreateChunks(per);

            for (int c = 0; c < _chunks.Length; c++)
            {
                if (!all && field.DirtyChunks[c] == 0) continue;
                field.DirtyChunks[c] = 0;
                int cx = c % per, cy = c / per;
                BuildChunk(env, b, field, _meshes[c], cx * field.ChunkSize, cy * field.ChunkSize, field.ChunkSize);
            }
        }

        void CreateChunks(int per)
        {
            DestroyChunks();
            _chunks = new GameObject[per * per];
            _meshes = new Mesh[per * per];
            for (int c = 0; c < _chunks.Length; c++)
            {
                var mesh = new Mesh { name = $"CoverChunk_{c}", indexFormat = IndexFormat.UInt32 };
                var go = new GameObject(mesh.name);
                go.transform.SetParent(_root, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = _material;
                mr.shadowCastingMode = ShadowCastingMode.Off;
                mr.receiveShadows = false;
                _chunks[c] = go;
                _meshes[c] = mesh;
            }
        }

        void BuildChunk(EnvGrid env, GameBalance b, GroundCoverField field, Mesh mesh, int x0, int y0, int cs)
        {
            _v.Clear(); _c.Clear(); _uv.Clear(); _t.Clear();
            int x1 = math.min(env.Size, x0 + cs), y1 = math.min(env.Size, y0 + cs);
            float thr = b.coverDrawThreshold;

            for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int i = env.Index(x, y);
                int type = env.CoverType[i];
                float amount = env.CoverAmount[i];
                if (type == 0 || amount < thr) continue;

                int mask = 0;
                if (Same(env, x, y + 1, type, thr)) mask |= BlobTiles.N;
                if (Same(env, x + 1, y + 1, type, thr)) mask |= BlobTiles.NE;
                if (Same(env, x + 1, y, type, thr)) mask |= BlobTiles.E;
                if (Same(env, x + 1, y - 1, type, thr)) mask |= BlobTiles.SE;
                if (Same(env, x, y - 1, type, thr)) mask |= BlobTiles.S;
                if (Same(env, x - 1, y - 1, type, thr)) mask |= BlobTiles.SW;
                if (Same(env, x - 1, y, type, thr)) mask |= BlobTiles.W;
                if (Same(env, x - 1, y + 1, type, thr)) mask |= BlobTiles.NW;
                Rect uv = BlobTiles.UvRect(BlobTiles.IndexOf(mask));

                float4 h = TerrainHeights.Corners(env, b, x, y) + Lift;
                Color col = field.Defs[type - 1].color;
                col.a = 0.25f + 0.75f * math.saturate((amount - thr) / (1f - thr));

                int s = _v.Count;
                _v.Add(new Vector3(x, h.x, y));
                _v.Add(new Vector3(x + 1, h.y, y));
                _v.Add(new Vector3(x + 1, h.z, y + 1));
                _v.Add(new Vector3(x, h.w, y + 1));
                _uv.Add(new Vector2(uv.xMin, uv.yMin));
                _uv.Add(new Vector2(uv.xMax, uv.yMin));
                _uv.Add(new Vector2(uv.xMax, uv.yMax));
                _uv.Add(new Vector2(uv.xMin, uv.yMax));
                for (int k = 0; k < 4; k++) _c.Add(col);
                _t.Add(s); _t.Add(s + 3); _t.Add(s + 2);
                _t.Add(s); _t.Add(s + 2); _t.Add(s + 1);
            }

            mesh.Clear();
            mesh.SetVertices(_v);
            mesh.SetColors(_c);
            mesh.SetUVs(0, _uv);
            mesh.SetTriangles(_t, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
        }

        static bool Same(EnvGrid env, int x, int y, int type, float thr)
        {
            if (!env.InBounds(x, y)) return true; // 地图边缘视为连通，避免边缘一圈缺口
            int i = env.Index(x, y);
            return env.CoverType[i] == type && env.CoverAmount[i] >= thr;
        }

        void DestroyChunks()
        {
            foreach (var go in _chunks) if (go != null) Object.Destroy(go);
            foreach (var m in _meshes) if (m != null) Object.Destroy(m);
            _chunks = Array.Empty<GameObject>();
            _meshes = Array.Empty<Mesh>();
        }

        public void Dispose()
        {
            DestroyChunks();
            if (_atlas != null) Object.Destroy(_atlas);
            if (_material != null) Object.Destroy(_material);
            if (_root != null) Object.Destroy(_root.gameObject);
        }
    }
}
