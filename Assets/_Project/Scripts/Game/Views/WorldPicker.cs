using System.Collections.Generic;
using Swarm.Data;
using Swarm.World;
using Unity.Mathematics;
using UnityEngine;

namespace Swarm.Views
{
    /// <summary>屏幕射线与世界求交：地形高度场、巨型实体（竖直圆柱）。笔刷、放置建筑、悬停虚化共用。</summary>
    public sealed class WorldPicker
    {
        /// <summary>巨型实体的拾取圆柱：xz 中心、地面高度、半径（树冠）、高度。</summary>
        public struct GiantBounds
        {
            public Vector2 Center;
            public float Ground, Radius, Height;
        }

        readonly EnvGrid _env;
        readonly GameBalance _balance;
        readonly List<GiantBounds> _giants = new List<GiantBounds>();

        public IReadOnlyList<GiantBounds> Giants => _giants;

        public WorldPicker(EnvGrid env, GameBalance balance, ResourceField resources, ResourceNodeDef[] defs)
        {
            _env = env;
            _balance = balance;
            for (int n = 0; n < resources.Nodes.Length; n++)
            {
                var node = resources.Nodes[n];
                if (node.Footprint <= 1) continue;
                var def = defs[node.Def];
                float s = node.Scale > 0f ? node.Scale : 1f;
                _giants.Add(new GiantBounds
                {
                    Center = new Vector2(node.Position.x, node.Position.y),
                    Ground = TerrainHeights.Sample(env, balance, node.Position),
                    Radius = Mathf.Max(def.size.x, def.size.z) * s * 0.5f,
                    Height = def.size.y * s,
                });
            }
        }

        /// <summary>沿射线步进求与地形的交点（先粗后细）。</summary>
        public bool PickTerrain(Ray ray, out Vector3 hit)
        {
            hit = default;
            const float step = 1f, maxDist = 700f;
            float prevT = 0f;
            for (float t = 0f; t < maxDist; t += step)
            {
                Vector3 p = ray.GetPoint(t);
                if (p.x < 0 || p.z < 0 || p.x >= _env.Size || p.z >= _env.Size) { prevT = t; continue; }
                float h = TerrainHeights.Sample(_env, _balance, new float2(p.x, p.z));
                if (p.y > h) { prevT = t; continue; }
                float lo = prevT, hi = t;
                for (int k = 0; k < 8; k++)
                {
                    float mid = (lo + hi) * 0.5f;
                    Vector3 m = ray.GetPoint(mid);
                    float hm = TerrainHeights.Sample(_env, _balance, new float2(m.x, m.z));
                    if (m.y > hm) lo = mid; else hi = mid;
                }
                hit = ray.GetPoint(hi);
                return true;
            }
            return false;
        }

        /// <summary>射线与竖直圆柱（侧面 + 顶面）求交，返回最近的巨型实体索引，没有则 −1。</summary>
        public int PickGiant(Ray ray, out float bestT)
        {
            bestT = float.MaxValue;
            int best = -1;
            Vector3 o = ray.origin, d = ray.direction;
            for (int i = 0; i < _giants.Count; i++)
            {
                var g = _giants[i];
                float ox = o.x - g.Center.x, oz = o.z - g.Center.y;
                float a = d.x * d.x + d.z * d.z;
                float b = 2f * (ox * d.x + oz * d.z);
                float c = ox * ox + oz * oz - g.Radius * g.Radius;
                float disc = b * b - 4f * a * c;
                if (a < 1e-6f || disc < 0f) continue;
                float t = (-b - Mathf.Sqrt(disc)) / (2f * a);
                if (t < 0f) continue;
                float top = g.Ground + g.Height;
                float y = o.y + t * d.y;
                if (y > top)
                {
                    if (d.y >= 0f) continue;
                    t = (top - o.y) / d.y; // 从顶面进入
                    float px = o.x + t * d.x - g.Center.x, pz = o.z + t * d.z - g.Center.y;
                    if (px * px + pz * pz > g.Radius * g.Radius) continue;
                }
                else if (y < g.Ground) continue;
                if (t < bestT) { bestT = t; best = i; }
            }
            return best;
        }

        public bool NearMountain(int x, int y)
        {
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
                if (_env.InBounds(x + dx, y + dy) && _env.IsObstacle(_env.Index(x + dx, y + dy))) return true;
            return false;
        }
    }
}
