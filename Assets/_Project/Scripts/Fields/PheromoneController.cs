using Swarm.Buildings;
using Swarm.Core;
using Swarm.Data;
using Swarm.Rendering;
using Swarm.UI;
using Swarm.World;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace Swarm.Fields
{
    /// <summary>
    /// M2：信息素场、覆盖范围、笔刷、归巢、热力图叠加的总控。挂在 World 物体上（WorldController 运行时自动添加）。
    /// 5 Hz：场更新 + 叠加层纹理上传；1 Hz：环境通道重算。
    /// 输入：1 / 2 / 3 / E 选笔刷，Esc 取消；左键涂抹；Ctrl+滚轮 半径，Shift+滚轮 强度；B 在光标处放信息素节点（调试）。
    /// </summary>
    [DefaultExecutionOrder(-400)]
    public sealed class PheromoneController : MonoBehaviour
    {
        public static PheromoneController Instance { get; private set; }

        const int OverlayCount = 11;
        const int OverlayHoming = 9, OverlayCoverage = 10;

        static readonly int HeatAId = Shader.PropertyToID("_SwarmHeatA");
        static readonly int HeatBId = Shader.PropertyToID("_SwarmHeatB");
        static readonly int HeatCId = Shader.PropertyToID("_SwarmHeatC");
        static readonly int MaskAId = Shader.PropertyToID("_SwarmHeatMaskA");
        static readonly int MaskBId = Shader.PropertyToID("_SwarmHeatMaskB");
        static readonly int MaskCId = Shader.PropertyToID("_SwarmHeatMaskC");
        static readonly int ColorsId = Shader.PropertyToID("_SwarmHeatColors");
        static readonly int MapSizeId = Shader.PropertyToID("_SwarmMapSize");
        static readonly int ShowCoverageId = Shader.PropertyToID("_SwarmShowCoverage");
        static readonly int CoverDimId = Shader.PropertyToID("_SwarmCoverDim");

        WorldController _world;
        PheromoneFields _fields;
        CoverageMap _coverage;
        BuildingSystem _buildings;
        Material _buildingMaterial;

        Texture2D _texA, _texB, _texC;
        NativeArray<half4> _packA, _packB, _packC;
        float4 _invA, _invB;
        float _invDanger;
        Vector4[] _colors;

        BrushTool _tool;
        float _radius = 4f, _strength = 1f;
        readonly bool[] _overlay = new bool[OverlayCount];

        CommandCard _card;
        LineRenderer _cursor;
        bool _subscribed, _cameraPlaced;

        public PheromoneFields Fields => _fields;
        public CoverageMap Coverage => _coverage;
        public BuildingSystem Buildings => _buildings;
        public BrushTool Tool => _tool;

        void Awake()
        {
            Instance = this;
            _world = GetComponent<WorldController>();
        }

        void Start()
        {
            _world.Generated += OnWorldGenerated;
            if (_world.Env != null) OnWorldGenerated();
            TrySubscribe();
            BuildCursor();
            BuildCard();
        }

        void OnDestroy()
        {
            if (_world != null) _world.Generated -= OnWorldGenerated;
            if (_subscribed && GameLoop.Instance != null)
            {
                GameLoop.Instance.OnTick5 -= Tick5;
                GameLoop.Instance.OnTick1 -= Tick1;
            }
            DisposeAll();
            _card?.Remove();
            if (_buildingMaterial != null) Destroy(_buildingMaterial);
            if (Instance == this) Instance = null;
        }

        void TrySubscribe()
        {
            if (_subscribed || GameLoop.Instance == null) return;
            GameLoop.Instance.OnTick5 += Tick5;
            GameLoop.Instance.OnTick1 += Tick1;
            _subscribed = true;
        }

        // ---------------------------------------------------------------- lifecycle

        void OnWorldGenerated()
        {
            DisposeAll();
            var env = _world.Env;
            var b = _world.Balance;

            if (_buildingMaterial == null)
            {
                var shader = Shader.Find("Swarm/Prop") ?? Shader.Find("Universal Render Pipeline/Lit");
                _buildingMaterial = new Material(shader);
            }
            _buildings = new BuildingSystem(transform, _buildingMaterial);
            var core = _buildings.PlaceNestCore(env, b, _world.Seed);
            _buildings.BuildPendingVisuals(env, b);

            _coverage = new CoverageMap(env.Size);
            _coverage.Recompute(_buildings.Buildings);

            _fields = new PheromoneFields(env, b);
            _fields.RecomputeHoming(_buildings.Entrances());
            _fields.RecomputeEnvironment(env, _world.Resources);

            _buildings.Changed += OnBuildingsChanged;

            int n = env.Length;
            _packA = new NativeArray<half4>(n, Allocator.Persistent);
            _packB = new NativeArray<half4>(n, Allocator.Persistent);
            _packC = new NativeArray<half4>(n, Allocator.Persistent);
            _texA = MakeTexture(env.Size, "HeatA");
            _texB = MakeTexture(env.Size, "HeatB");
            _texC = MakeTexture(env.Size, "HeatC");
            Shader.SetGlobalTexture(HeatAId, _texA);
            Shader.SetGlobalTexture(HeatBId, _texB);
            Shader.SetGlobalTexture(HeatCId, _texC);
            Shader.SetGlobalFloat(MapSizeId, env.Size);
            CacheChannelDisplay(b);
            Upload();

            var rts = FindFirstObjectByType<RtsCamera>();
            if (rts != null) rts.JumpTo(new Vector2(core.Tile.x, core.Tile.y));
        }

        static Texture2D MakeTexture(int size, string name) =>
            new Texture2D(size, size, TextureFormat.RGBAHalf, false, true)
            {
                name = name,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };

        void CacheChannelDisplay(GameBalance b)
        {
            var defaults = GameBalance.DefaultPheromoneChannels();
            float Inv(PheromoneChannel c)
            {
                foreach (var s in b.pheromoneChannels ?? defaults)
                    if (s.channel == c) return 1f / math.max(0.01f, s.displayScale);
                return 1f;
            }
            Color Col(PheromoneChannel c)
            {
                foreach (var s in b.pheromoneChannels ?? defaults)
                    if (s.channel == c) return s.color;
                return Color.white;
            }
            _invA = new float4(Inv(PheromoneChannel.Gather), Inv(PheromoneChannel.Rally), Inv(PheromoneChannel.Avoid), Inv(PheromoneChannel.Trail));
            _invB = new float4(Inv(PheromoneChannel.Alarm), Inv(PheromoneChannel.ScentProtein), Inv(PheromoneChannel.ScentMineral), Inv(PheromoneChannel.ScentBuild));
            _invDanger = Inv(PheromoneChannel.Danger);

            _colors = new Vector4[12];
            for (int c = 0; c < Pheromones.ChannelCount; c++) _colors[c] = Col((PheromoneChannel)c);
            _colors[OverlayHoming] = new Color(0.25f, 0.55f, 1f);
            _colors[OverlayCoverage] = new Color(0.4f, 1f, 0.9f);
            Shader.SetGlobalVectorArray(ColorsId, _colors);
        }

        void OnBuildingsChanged()
        {
            _coverage.Recompute(_buildings.Buildings);
            _fields.RecomputeHoming(_buildings.Entrances());
            _buildings.BuildPendingVisuals(_world.Env, _world.Balance);
            Upload();
        }

        void DisposeAll()
        {
            if (_buildings != null) { _buildings.Changed -= OnBuildingsChanged; _buildings.Dispose(); _buildings = null; }
            _coverage?.Dispose(); _coverage = null;
            _fields?.Dispose(); _fields = null;
            if (_packA.IsCreated) _packA.Dispose();
            if (_packB.IsCreated) _packB.Dispose();
            if (_packC.IsCreated) _packC.Dispose();
            if (_texA != null) Destroy(_texA);
            if (_texB != null) Destroy(_texB);
            if (_texC != null) Destroy(_texC);
        }

        // ---------------------------------------------------------------- ticks

        void Tick5(float dt)
        {
            if (_fields == null) return;
            _fields.Step(dt, _coverage.Covered);
            Upload();
        }

        void Tick1(float dt)
        {
            if (_fields == null) return;
            _fields.RecomputeEnvironment(_world.Env, _world.Resources);
        }

        void Upload()
        {
            if (_fields == null || _texA == null) return;
            _fields.Pack(_packA, _packB, _packC, _coverage.Covered, _invA, _invB, _invDanger, _world.Balance.homingDisplayRange);
            _texA.SetPixelData(_packA, 0); _texA.Apply(false);
            _texB.SetPixelData(_packB, 0); _texB.Apply(false);
            _texC.SetPixelData(_packC, 0); _texC.Apply(false);
        }

        // ---------------------------------------------------------------- input & brush

        void Update()
        {
            TrySubscribe();
            if (_fields == null) return;
            var b = _world.Balance;
            HandleKeys(b);

            bool picked = false;
            Vector3 hit = default;
            var mouse = Mouse.current;
            var cam = Camera.main;
            bool overUi = _card != null && _card.PointerOver;
            if (mouse != null && cam != null && !overUi)
                picked = _world.PickTerrain(cam.ScreenPointToRay(mouse.position.ReadValue()), out hit);

            if (picked && _tool != BrushTool.None && mouse.leftButton.isPressed)
            {
                float2 c = new float2(hit.x, hit.z);
                if (_tool == BrushTool.Erase) _fields.Erase(c, _radius);
                else _fields.Paint(ChannelOf(_tool), c, _radius, _strength, _coverage.Covered);
            }

            UpdateCursor(picked && _tool != BrushTool.None, hit);
            PushOverlayGlobals();
            _card?.SetInfo(
                $"Brush {_tool}  r {_radius:0.0}  s {_strength:0.00}   |   field step {_fields.LastStepMs:0.00} ms (5 Hz)  " +
                $"env {_fields.LastEnvironmentMs:0.0} ms (1 Hz)   |   coverage {_coverage.CoveredTiles} tiles, buildings {_buildings.Buildings.Count}");
        }

        void HandleKeys(GameBalance b)
        {
            var kb = Keyboard.current;
            if (kb == null) return;
            if (kb.digit1Key.wasPressedThisFrame) SelectTool(BrushTool.Gather);
            if (kb.digit2Key.wasPressedThisFrame) SelectTool(BrushTool.Rally);
            if (kb.digit3Key.wasPressedThisFrame) SelectTool(BrushTool.Avoid);
            if (kb.eKey.wasPressedThisFrame) SelectTool(BrushTool.Erase);
            if (kb.escapeKey.wasPressedThisFrame) SelectTool(BrushTool.None);
            if (kb.bKey.wasPressedThisFrame) PlaceNodeAtCursor();

            var mouse = Mouse.current;
            if (mouse == null) return;
            float scroll = mouse.scroll.ReadValue().y;
            if (Mathf.Abs(scroll) > 10f) scroll /= 120f;
            if (scroll == 0f) return;
            if (kb.ctrlKey.isPressed)
            {
                _radius = Mathf.Clamp(_radius + scroll, b.brushRadiusMin, b.brushRadiusMax);
                _card?.SetRadius(_radius);
            }
            else if (kb.shiftKey.isPressed)
            {
                _strength = Mathf.Clamp(_strength + scroll * 0.1f, b.brushStrengthMin, b.brushStrengthMax);
                _card?.SetStrength(_strength);
            }
        }

        void SelectTool(BrushTool tool)
        {
            _tool = tool;
            _card?.SetTool(tool);
        }

        void PlaceNodeAtCursor()
        {
            var mouse = Mouse.current;
            var cam = Camera.main;
            if (mouse == null || cam == null) return;
            if (!_world.PickTerrain(cam.ScreenPointToRay(mouse.position.ReadValue()), out var hit)) return;
            var tile = new int2((int)hit.x, (int)hit.z);
            if (!_buildings.TryPlace(BuildingType.PheromoneNode, tile, _world.Env, _coverage, _world.Balance))
                Debug.Log($"[Swarm] Cannot place pheromone node at {tile}: must be inside coverage, on walkable ground, ≥3 tiles from other buildings.");
        }

        static PheromoneChannel ChannelOf(BrushTool t) => t switch
        {
            BrushTool.Rally => PheromoneChannel.Rally,
            BrushTool.Avoid => PheromoneChannel.Avoid,
            _ => PheromoneChannel.Gather,
        };

        // ---------------------------------------------------------------- overlay

        void PushOverlayGlobals()
        {
            bool[] show = new bool[OverlayCount];
            for (int k = 0; k < OverlayCount; k++) show[k] = _overlay[k];
            // 选中笔刷时自动显示对应通道和覆盖边界
            if (_tool == BrushTool.Gather) show[0] = true;
            if (_tool == BrushTool.Rally) show[1] = true;
            if (_tool == BrushTool.Avoid) show[2] = true;
            if (_tool == BrushTool.Erase) { show[0] = show[1] = show[2] = true; }
            if (_tool != BrushTool.None) show[OverlayCoverage] = true;

            float F(bool v) => v ? 1f : 0f;
            Shader.SetGlobalVector(MaskAId, new Vector4(F(show[0]), F(show[1]), F(show[2]), F(show[3])));
            Shader.SetGlobalVector(MaskBId, new Vector4(F(show[4]), F(show[5]), F(show[6]), F(show[7])));
            Shader.SetGlobalVector(MaskCId, new Vector4(F(show[8]), 0f, F(show[OverlayHoming]), 0f));
            Shader.SetGlobalFloat(ShowCoverageId, F(show[OverlayCoverage]));

            bool any = false;
            for (int k = 0; k < OverlayCoverage; k++) any |= show[k];
            Shader.SetGlobalFloat(CoverDimId, any ? 1f : 0f);
        }

        // ---------------------------------------------------------------- cursor & UI

        void BuildCursor()
        {
            var go = new GameObject("BrushCursor");
            go.transform.SetParent(transform, false);
            _cursor = go.AddComponent<LineRenderer>();
            _cursor.loop = true;
            _cursor.positionCount = 48;
            _cursor.widthMultiplier = 0.18f;
            _cursor.useWorldSpace = true;
            _cursor.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _cursor.receiveShadows = false;
            var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Universal Render Pipeline/Unlit");
            _cursor.sharedMaterial = new Material(shader);
            _cursor.enabled = false;
        }

        void UpdateCursor(bool visible, Vector3 hit)
        {
            if (_cursor == null) return;
            _cursor.enabled = visible;
            if (!visible) return;
            var env = _world.Env;
            var b = _world.Balance;
            Color col = _tool switch
            {
                BrushTool.Gather => new Color(0.3f, 1f, 0.35f),
                BrushTool.Rally => new Color(1f, 0.3f, 0.25f),
                BrushTool.Avoid => new Color(0.75f, 0.4f, 1f),
                _ => new Color(1f, 1f, 1f),
            };
            bool covered = _coverage.IsCovered((int)hit.x, (int)hit.z);
            if (!covered && _tool != BrushTool.Erase) col = new Color(0.5f, 0.5f, 0.5f); // 覆盖外：灰色，表示无效
            _cursor.startColor = _cursor.endColor = col;
            for (int k = 0; k < _cursor.positionCount; k++)
            {
                float a = k / (float)_cursor.positionCount * Mathf.PI * 2f;
                float2 p = new float2(hit.x + Mathf.Cos(a) * _radius, hit.z + Mathf.Sin(a) * _radius);
                float y = TerrainHeights.Sample(env, b, p) + 0.2f;
                _cursor.SetPosition(k, new Vector3(p.x, y, p.y));
            }
        }

        void BuildCard()
        {
            var doc = FindFirstObjectByType<UIDocument>();
            if (doc == null) return;
            var b = _world.Balance;
            var colors = new Color[OverlayCount];
            if (_colors == null) CacheChannelDisplay(b);
            for (int k = 0; k < OverlayCount; k++) colors[k] = _colors[k];
            _card = new CommandCard(doc.rootVisualElement, b.brushRadiusMin, b.brushRadiusMax,
                b.brushStrengthMin, b.brushStrengthMax, colors);
            _card.ToolSelected += SelectTool;
            _card.RadiusChanged += v => _radius = v;
            _card.StrengthChanged += v => _strength = v;
            _card.OverlayToggled += (k, on) => _overlay[k] = on;
            _card.PlaceNodeClicked += () => Debug.Log("[Swarm] Press B with the mouse over the ground to place a pheromone node.");
            _card.SetRadius(_radius);
            _card.SetStrength(_strength);
            _card.SetTool(_tool);
        }

        public string DebugText() => _fields == null
            ? "Pheromones: -"
            : $"Pheromone step {_fields.LastStepMs:0.00} ms   env {_fields.LastEnvironmentMs:0.0} ms   coverage {_coverage.CoveredTiles}";
    }
}
