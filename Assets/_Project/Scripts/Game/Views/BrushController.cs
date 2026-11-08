using System;
using Swarm.Buildings;
using Swarm.Core;
using Swarm.Data;
using Swarm.Input;
using Swarm.UI;
using Swarm.World;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace Swarm.Views
{
    /// <summary>
    /// 信息素笔刷（GDD 4.3）与命令卡：选笔刷、调半径 / 强度、按住左键涂抹、B 放信息素节点（调试）。
    /// 跨世界重建保留：工具、半径、强度、叠加层开关；每次生成新世界后 Bind 一次。
    /// 只通过 SimulationWorld 的公开方法下达意图，不直接改场数据。
    /// </summary>
    public sealed class BrushController : IDisposable
    {
        readonly GameBalance _balance;
        readonly LineRenderer _cursor;
        readonly Material _cursorMaterial;
        readonly bool[] _overlayToggles = new bool[PheromoneOverlay.Count];
        CommandCard _card;

        SimulationWorld _world;
        WorldPicker _picker;
        PheromoneOverlay _overlay;

        BrushTool _tool;
        float _radius = 4f, _strength = 1f;

        public BrushTool Tool => _tool;

        public BrushController(Transform parent, GameBalance balance)
        {
            _balance = balance;
            var go = new GameObject("BrushCursor");
            go.transform.SetParent(parent, false);
            _cursor = go.AddComponent<LineRenderer>();
            _cursor.loop = true;
            _cursor.positionCount = 48;
            _cursor.widthMultiplier = 0.18f;
            _cursor.useWorldSpace = true;
            _cursor.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _cursor.receiveShadows = false;
            var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Universal Render Pipeline/Unlit");
            _cursorMaterial = new Material(shader);
            _cursor.sharedMaterial = _cursorMaterial;
            _cursor.enabled = false;
        }

        public void Bind(SimulationWorld world, WorldPicker picker, PheromoneOverlay overlay)
        {
            _world = world;
            _picker = picker;
            _overlay = overlay;
        }

        /// <summary>在场景里已有的 UIDocument 上创建命令卡（只建一次）。</summary>
        public void BuildCard(UIDocument doc, Color[] overlayColors)
        {
            if (doc == null || _card != null) return;
            _card = new CommandCard(doc.rootVisualElement, _balance.brushRadiusMin, _balance.brushRadiusMax,
                _balance.brushStrengthMin, _balance.brushStrengthMax, overlayColors);
            _card.ToolSelected += SelectTool;
            _card.RadiusChanged += v => _radius = v;
            _card.StrengthChanged += v => _strength = v;
            _card.OverlayToggled += (k, on) => _overlayToggles[k] = on;
            _card.PlaceNodeClicked += () => Debug.Log("[Swarm] Press B with the mouse over the ground to place a pheromone node.");
            _card.SetRadius(_radius);
            _card.SetStrength(_strength);
            _card.SetTool(_tool);
        }

        public void Update(Camera cam)
        {
            if (_world == null) return;
            HandleKeys();
            SwarmInput.PointerOverUi = _card != null && _card.PointerOver;

            bool picked = false;
            Vector3 hit = default;
            if (cam != null && SwarmInput.HasPointer && !SwarmInput.PointerOverUi)
                picked = _picker.PickTerrain(cam.ScreenPointToRay(SwarmInput.PointerPosition), out hit);

            if (picked && _tool != BrushTool.None && SwarmInput.PrimaryHeld)
            {
                var c = new float2(hit.x, hit.z);
                if (_tool == BrushTool.Erase) _world.ErasePheromone(c, _radius);
                else _world.PaintPheromone(ChannelOf(_tool), c, _radius, _strength);
                // 绘制层写入后，场在下一个 5 Hz 步向它靠拢，届时叠加层再上传

            }

            if (picked && SwarmInput.Pressed(SwarmAction.DebugPlaceNode)) PlaceNode(hit);

            UpdateCursor(picked && _tool != BrushTool.None, hit);
            _overlay?.PushGlobals(_tool, _overlayToggles);
            var p = _world.Pheromones;
            _card?.SetInfo(
                $"Brush {_tool}  r {_radius:0.0}  s {_strength:0.00}   |   field step {p.LastStepMs:0.00} ms (5 Hz)  " +
                $"env {p.LastEnvironmentMs:0.0} ms (1 Hz)   |   coverage {_world.Coverage.CoveredTiles} tiles, buildings {_world.Buildings.Buildings.Count}");
        }

        void HandleKeys()
        {
            if (SwarmInput.Pressed(SwarmAction.BrushGather)) SelectTool(BrushTool.Gather);
            if (SwarmInput.Pressed(SwarmAction.BrushRally)) SelectTool(BrushTool.Rally);
            if (SwarmInput.Pressed(SwarmAction.BrushAvoid)) SelectTool(BrushTool.Avoid);
            if (SwarmInput.Pressed(SwarmAction.BrushErase)) SelectTool(BrushTool.Erase);
            if (SwarmInput.Pressed(SwarmAction.BrushCancel)) SelectTool(BrushTool.None);

            float scroll = SwarmInput.ScrollSteps;
            if (scroll == 0f) return;
            if (SwarmInput.CtrlHeld)
            {
                _radius = Mathf.Clamp(_radius + scroll, _balance.brushRadiusMin, _balance.brushRadiusMax);
                _card?.SetRadius(_radius);
            }
            else if (SwarmInput.ShiftHeld)
            {
                _strength = Mathf.Clamp(_strength + scroll * 0.1f, _balance.brushStrengthMin, _balance.brushStrengthMax);
                _card?.SetStrength(_strength);
            }
        }

        void SelectTool(BrushTool tool)
        {
            _tool = tool;
            _card?.SetTool(tool);
        }

        void PlaceNode(Vector3 hit)
        {
            var tile = new int2((int)hit.x, (int)hit.z);
            if (!_world.TryPlaceBuilding(BuildingType.PheromoneNode, tile))
                Debug.Log($"[Swarm] Cannot place pheromone node at {tile}: must be inside coverage, on walkable ground, ≥3 tiles from other buildings.");
        }

        static PheromoneChannel ChannelOf(BrushTool t) => t switch
        {
            BrushTool.Rally => PheromoneChannel.Rally,
            BrushTool.Avoid => PheromoneChannel.Avoid,
            _ => PheromoneChannel.Gather,
        };

        void UpdateCursor(bool visible, Vector3 hit)
        {
            _cursor.enabled = visible;
            if (!visible) return;
            Color col = _tool switch
            {
                BrushTool.Gather => new Color(0.3f, 1f, 0.35f),
                BrushTool.Rally => new Color(1f, 0.3f, 0.25f),
                BrushTool.Avoid => new Color(0.75f, 0.4f, 1f),
                _ => new Color(1f, 1f, 1f),
            };
            bool covered = _world.Coverage.IsCovered((int)hit.x, (int)hit.z);
            if (!covered && _tool != BrushTool.Erase) col = new Color(0.5f, 0.5f, 0.5f); // 覆盖外：灰色，表示无效
            _cursor.startColor = _cursor.endColor = col;
            for (int k = 0; k < _cursor.positionCount; k++)
            {
                float a = k / (float)_cursor.positionCount * Mathf.PI * 2f;
                var p = new float2(hit.x + Mathf.Cos(a) * _radius, hit.z + Mathf.Sin(a) * _radius);
                float y = TerrainHeights.Sample(_world.Env, _balance, p) + 0.2f;
                _cursor.SetPosition(k, new Vector3(p.x, y, p.y));
            }
        }

        public void Dispose()
        {
            _card?.Remove();
            if (_cursor != null) Object.Destroy(_cursor.gameObject);
            if (_cursorMaterial != null) Object.Destroy(_cursorMaterial);
        }
    }
}
