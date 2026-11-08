using System;
using System.Collections.Generic;
using Swarm.Buildings;
using Swarm.Data;
using Swarm.World;
using Unity.Mathematics;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Swarm.Rendering
{
    /// <summary>
    /// 建筑灰盒外观（GDD 10）。建筑数量很少，用 GameObject 显示；Sync 按 BuildingSystem 列表补建新建筑的外观。
    /// 建筑被摧毁（M7）时改为按列表整体对齐。
    /// </summary>
    public sealed class BuildingRenderer : IDisposable
    {
        readonly Transform _root;
        readonly Material _template;
        readonly List<GameObject> _visuals = new List<GameObject>();
        readonly List<Material> _materials = new List<Material>();

        public BuildingRenderer(Transform parent, Material template)
        {
            _root = new GameObject("Buildings").transform;
            _root.SetParent(parent, false);
            _template = template;
        }

        public void Sync(IReadOnlyList<Building> buildings, EnvGrid env, GameBalance b)
        {
            for (int i = _visuals.Count; i < buildings.Count; i++)
                _visuals.Add(CreateVisual(buildings[i], env, b));
        }

        GameObject CreateVisual(Building bld, EnvGrid env, GameBalance b)
        {
            var go = new GameObject(bld.Type.ToString());
            go.transform.SetParent(_root, false);
            float ground = TerrainHeights.Sample(env, b, new float2(bld.Tile) + 0.5f);
            go.transform.position = new Vector3(bld.Tile.x + 0.5f, ground, bld.Tile.y + 0.5f);

            switch (bld.Type)
            {
                case BuildingType.NestCore:
                    Part(go, PrimitiveType.Sphere, new Vector3(0, 0.6f, 0), new Vector3(6f, 3.2f, 6f), new Color(0.45f, 0.32f, 0.22f));
                    Part(go, PrimitiveType.Sphere, new Vector3(0.6f, 1.6f, 0.4f), new Vector3(3.6f, 2.6f, 3.6f), new Color(0.52f, 0.38f, 0.26f));
                    Part(go, PrimitiveType.Cylinder, new Vector3(-1.6f, 0.05f, -1.6f), new Vector3(1.4f, 0.06f, 1.4f), new Color(0.08f, 0.06f, 0.05f));
                    break;
                case BuildingType.PheromoneNode:
                    Part(go, PrimitiveType.Capsule, new Vector3(0, 1.1f, 0), new Vector3(0.7f, 1.1f, 0.7f), new Color(0.35f, 0.55f, 0.3f));
                    Part(go, PrimitiveType.Sphere, new Vector3(0, 2.3f, 0), new Vector3(1f, 1f, 1f), new Color(0.6f, 1f, 0.5f));
                    break;
                default:
                    Part(go, PrimitiveType.Cube, new Vector3(0, 0.75f, 0), new Vector3(1.5f, 1.5f, 1.5f), new Color(0.5f, 0.5f, 0.5f));
                    break;
            }
            return go;
        }

        void Part(GameObject parent, PrimitiveType type, Vector3 pos, Vector3 scale, Color color)
        {
            var p = GameObject.CreatePrimitive(type);
            Object.Destroy(p.GetComponent<Collider>());
            p.transform.SetParent(parent.transform, false);
            p.transform.localPosition = pos;
            p.transform.localScale = scale;
            var mat = new Material(_template);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            mat.color = color;
            p.GetComponent<MeshRenderer>().sharedMaterial = mat;
            _materials.Add(mat);
        }

        public void Dispose()
        {
            foreach (var m in _materials) if (m != null) Object.Destroy(m);
            _materials.Clear();
            _visuals.Clear();
            if (_root != null) Object.Destroy(_root.gameObject);
        }
    }
}
