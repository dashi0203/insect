using Swarm.Data;
using Swarm.Input;
using Swarm.Rendering;
using Swarm.UI;
using Swarm.Views;
using UnityEngine;
using UnityEngine.UIElements;

namespace Swarm.Core
{
    /// <summary>
    /// 组合根：创建 SimulationWorld，把 GameLoop 的节拍接到 SimulationWorld.Step，并创建只读模拟数据的各个视图。
    /// 模拟状态全部在 SimulationWorld 里；这里只做装配、调试按键和每帧的渲染驱动。
    /// 调试键：Tab 切换区域，N 新种子，R 同种子重新生成，H 取空全部资源，] 下一季节，M 地形调试着色，
    /// K 生成 500 只工虫（Shift + K 生成 5000 只）。
    /// </summary>
    [DefaultExecutionOrder(-500)]
    public sealed class GameRoot : MonoBehaviour
    {
        [Header("Config (GDD 14.5)")]
        [SerializeField] GameBalance balance;
        [SerializeField] RegionDef[] regions;
        [SerializeField] ResourceNodeDef[] resourceDefs;
        [SerializeField] GroundCoverDef[] coverDefs;

        [Header("World")]
        [SerializeField] int regionIndex;
        [SerializeField] uint seed = 12345;

        [Header("Materials")]
        [SerializeField] Material terrainMaterial;
        [SerializeField] Material groundCoverMaterial;
        [SerializeField] Material resourceMaterial;

        [Header("Scene (found automatically when empty)")]
        [SerializeField] RtsCamera cameraRig;
        [SerializeField] UIDocument uiDocument;

        static readonly int SnowId = Shader.PropertyToID("_SwarmSnow");

        SimContent _content;
        SimulationWorld _world;
        GameLoop _loop;
        Camera _cam;
        TerrainOverlay _terrainOverlay;
        Material _buildingMaterial;

        // 每个世界一套的视图
        TerrainMesher _terrain;
        GroundCoverRenderer _coverRenderer;
        ResourceRenderer _resourceRenderer;
        BuildingRenderer _buildingRenderer;
        AgentRenderer _agentRenderer;
        PheromoneOverlay _overlay;
        WorldPicker _picker;
        HoverFade _hover;
        // 跨世界保留
        BrushController _brush;

        public SimulationWorld World => _world;

        /// <summary>每次世界（重新）生成并建好视图后触发。</summary>
        public event System.Action WorldCreated;

        void Awake()
        {
            _content = new SimContent { Balance = balance, Regions = regions, Resources = resourceDefs, Covers = coverDefs };
            if (!_content.IsComplete)
            {
                var d = SimContent.Defaults();
                if (_content.Balance == null) _content.Balance = d.Balance;
                if (_content.Regions == null || _content.Regions.Length == 0) _content.Regions = d.Regions;
                if (_content.Resources == null || _content.Resources.Length == 0) _content.Resources = d.Resources;
                if (_content.Covers == null || _content.Covers.Length == 0) _content.Covers = d.Covers;
                Debug.LogWarning("[Swarm] GameRoot is missing config assets; using built-in GDD defaults.", this);
            }
            if (cameraRig == null) cameraRig = FindFirstObjectByType<RtsCamera>();
            if (uiDocument == null) uiDocument = FindFirstObjectByType<UIDocument>();
            _brush = new BrushController(transform, _content.Balance);
        }

        void OnEnable() => DebugInfo.Register(DebugText);
        void OnDisable() => DebugInfo.Unregister(DebugText);

        void Start()
        {
            CreateWorld();
            BindLoop();
        }

        void OnDestroy()
        {
            if (_loop != null) _loop.OnStep -= OnStep;
            DisposeWorld();
            _brush?.Dispose();
            if (_buildingMaterial != null) Destroy(_buildingMaterial);
        }

        void BindLoop()
        {
            if (_loop != null || GameLoop.Instance == null) return;
            _loop = GameLoop.Instance;
            _loop.OnStep += OnStep;
        }

        // ---------------------------------------------------------------- world lifecycle

        public void CreateWorld()
        {
            DisposeWorld();
            regionIndex = Mathf.Clamp(regionIndex, 0, _content.Regions.Length - 1);
            _world = SimulationWorld.Create(_content, regionIndex, seed);
            var b = _world.Balance;

            _terrain = new TerrainMesher(transform, terrainMaterial != null ? terrainMaterial : FallbackMaterial("Swarm/Terrain"))
            {
                Overlay = _terrainOverlay,
            };
            _terrain.BuildAll(_world.Env, b, _world.Region);
            _coverRenderer = new GroundCoverRenderer(transform,
                groundCoverMaterial != null ? groundCoverMaterial : FallbackMaterial("Swarm/GroundCover"));
            _coverRenderer.Rebuild(_world.Env, b, _world.Cover, true);
            _resourceRenderer = new ResourceRenderer(_content.Resources,
                resourceMaterial != null ? resourceMaterial : FallbackMaterial("Swarm/Prop"));
            _agentRenderer = new AgentRenderer(_world.Env, b,
                resourceMaterial != null ? resourceMaterial : FallbackMaterial("Swarm/Prop"));

            if (_buildingMaterial == null) _buildingMaterial = FallbackMaterial("Swarm/Prop", "Universal Render Pipeline/Lit");
            _buildingRenderer = new BuildingRenderer(transform, _buildingMaterial);
            _buildingRenderer.Sync(_world.Buildings.Buildings, _world.Env, b);

            _overlay = new PheromoneOverlay(_world);
            _picker = new WorldPicker(_world.Env, b, _world.Resources, _content.Resources);
            _hover = new HoverFade(_picker, _world.Env.Size, b.mountainFadeRadius);
            _brush.Bind(_world, _picker, _overlay);
            _brush.BuildCard(uiDocument, _overlay.Colors);

            _world.CoverStepped += OnCoverStepped;
            _world.BuildingsChanged += OnBuildingsChanged;

            PlaceCamera();
            var s = _world.Stats;
            Debug.Log($"[Swarm] World generated: region={_world.Region.id} seed={_world.Seed} hash={s.MapHash:X16} " +
                      $"nodes={_world.Resources.Nodes.Length} giants={_world.Resources.CountGiant} superFertile={s.SuperFertileTiles} " +
                      $"mountain={s.MountainPercent:0.0}% cliff={s.CliffPercent:0.0}% water={s.WaterPercent:0.0}% in {s.GenerateMs:0} ms");
            WorldCreated?.Invoke();
        }

        void PlaceCamera()
        {
            if (cameraRig == null) return;
            int size = _world.Env.Size;
            cameraRig.SetBounds(Vector2.zero, new Vector2(size, size));
            if (_world.Buildings.TryGetNestCore(out var core)) cameraRig.JumpTo(new Vector2(core.Tile.x, core.Tile.y));
            else cameraRig.JumpTo(new Vector2(size * 0.5f, size * 0.5f));
        }

        void DisposeWorld()
        {
            if (_world != null)
            {
                _world.CoverStepped -= OnCoverStepped;
                _world.BuildingsChanged -= OnBuildingsChanged;
            }
            _brush?.Bind(null, null, null);
            _overlay?.Dispose(); _overlay = null;
            _buildingRenderer?.Dispose(); _buildingRenderer = null;
            _resourceRenderer?.Dispose(); _resourceRenderer = null;
            _agentRenderer?.Dispose(); _agentRenderer = null;
            _coverRenderer?.Dispose(); _coverRenderer = null;
            _terrain?.Dispose(); _terrain = null;
            _picker = null;
            _hover = null;
            _world?.Dispose(); _world = null;
        }

        static Material FallbackMaterial(string shaderName, string fallback = "Universal Render Pipeline/Unlit")
        {
            var shader = Shader.Find(shaderName);
            if (shader == null) shader = Shader.Find(fallback);
            return new Material(shader);
        }

        // ---------------------------------------------------------------- simulation hooks

        void OnStep(TickFlags flags)
        {
            if (_world == null) return;
            _world.Step(flags);
            if ((flags & TickFlags.Hz5) != 0) _overlay?.MarkDirty();
        }

        void OnCoverStepped() => _coverRenderer?.Rebuild(_world.Env, _world.Balance, _world.Cover, false);

        void OnBuildingsChanged()
        {
            _buildingRenderer?.Sync(_world.Buildings.Buildings, _world.Env, _world.Balance);
            _overlay?.MarkDirty();
        }

        // ---------------------------------------------------------------- frame

        void Update()
        {
            BindLoop();
            HandleDebugInput();
            if (_world == null) return;
            if (_cam == null) _cam = Camera.main;

            Shader.SetGlobalFloat(SnowId, Mathf.Clamp01((1f - (_world.Region.baseTemperature + _world.Season.TemperatureOffset)) / 6f));
            _brush.Update(_cam);
            _hover.Update(_cam);
            _overlay.UploadIfDirty();
            _resourceRenderer.Draw(_world.Resources, _world.Env, _world.Balance, _cam, _world.Time);
            _agentRenderer.Draw(_world.Agents, _loop != null ? _loop.RenderAlpha : 1f, _cam);
        }

        void HandleDebugInput()
        {
            if (SwarmInput.Pressed(SwarmAction.DebugNextRegion))
            {
                regionIndex = (regionIndex + 1) % _content.Regions.Length;
                CreateWorld();
            }
            if (SwarmInput.Pressed(SwarmAction.DebugNewSeed))
            {
                seed = (uint)Random.Range(1, int.MaxValue);
                CreateWorld();
            }
            if (SwarmInput.Pressed(SwarmAction.DebugRegenerate)) CreateWorld();
            if (_world == null) return;
            if (SwarmInput.Pressed(SwarmAction.DebugTerrainOverlay) && _terrain != null)
            {
                _terrainOverlay = (TerrainOverlay)(((int)_terrainOverlay + 1) % 3);
                _terrain.Overlay = _terrainOverlay;
                _terrain.BuildAll(_world.Env, _world.Balance, _world.Region);
            }
            if (SwarmInput.Pressed(SwarmAction.DebugDepleteResources)) _world.DebugDepleteAllResources();
            if (SwarmInput.Pressed(SwarmAction.DebugNextSeason)) _world.DebugSkipSeason();
            if (SwarmInput.Pressed(SwarmAction.DebugSpawnWorkers))
                Debug.Log($"[Swarm] Spawned {_world.SpawnWorkers(SwarmInput.ShiftHeld ? 5000 : 500)} workers (total {_world.Agents.Count}).");
        }

        string DebugText()
        {
            if (_world == null) return "World: -";
            var w = _world;
            var r = w.Region;
            var s = w.Stats;
            var res = w.Resources;
            string cover = "";
            for (int t = 0; t < _content.Covers.Length; t++)
                cover += $"{_content.Covers[t].id} {w.Cover.VisibleTiles[t]}  ";
            int search = 0, harvest = 0, ret = 0;
            var agents = w.Agents.Agents;
            for (int i = 0; i < agents.Length; i++)
            {
                var st = (Swarm.Agents.AgentState)agents[i].State;
                if (st == Swarm.Agents.AgentState.WorkerSearch) search++;
                else if (st == Swarm.Agents.AgentState.WorkerHarvest) harvest++;
                else if (st == Swarm.Agents.AgentState.WorkerReturn) ret++;
            }
            var col = w.Colony;
            string stock = "";
            for (int k = 0; k < 3; k++)
                stock += $"{(ResourceKind)k} {col.Stock[k]:0}/{col.Capacity[k]:0} ({col.NetChange60(k):+0;-0}/60s)  ";
            string systems = "";
            foreach (var kv in w.SystemMs) systems += $"{kv.Key} {kv.Value:0.00}  ";
            return
                $"Region {r.id}  seed {w.Seed}  hash {s.MapHash:X16}  gen {s.GenerateMs:0}ms\n" +
                $"Year {w.Season.Year + 1} {w.Season.Current} {w.Season.SeasonProgress * 100f:0}%   " +
                $"season dT {w.Season.TemperatureOffset:+0.0;-0.0}C   region T {r.baseTemperature + w.Season.TemperatureOffset:0.0}C\n" +
                $"Terrain  mountain {s.MountainPercent:0}%  cliff {s.CliffPercent:0}%  water {s.WaterPercent:0}%   giants {res.CountGiant}   super-fertile tiles {s.SuperFertileTiles}\n" +
                $"Nodes {res.Nodes.Length}  full {res.CountFull}  regrowing {res.CountRegrowing}  waiting {res.CountWaiting}  dead {res.CountDead}   " +
                $"drawn chunks {_resourceRenderer?.VisibleChunks}  instances {_resourceRenderer?.DrawnInstances}\n" +
                $"Cover tiles  {cover}\n" +
                $"Workers {agents.Length}  search {search}  harvest {harvest}  return {ret}   drawn {_agentRenderer?.Drawn}\n" +
                $"Stock  {stock}\n" +
                $"Systems ms  {systems}\n" +
                "Tab region   N new seed   R regen   H harvest all   ] next season   K +500 workers (Shift x10)   M overlay: " + _terrainOverlay;
        }
    }
}
