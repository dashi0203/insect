using Swarm.Core;
using Swarm.Data;
using Swarm.Rendering;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Swarm.World
{
    /// <summary>
    /// M1 世界：生成环境网格 / 地形 / 资源实体 / 地被，并在 1 Hz 节拍上推进季节、资源再生、地被元胞自动机。
    /// 调试键：Tab 切换区域，N 换新种子，R 用同一种子重新生成，H 取空全部资源，] 跳到下一季节。
    /// </summary>
    [DefaultExecutionOrder(-500)]
    public sealed class WorldController : MonoBehaviour
    {
        public static WorldController Instance { get; private set; }

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

        static readonly int SnowId = Shader.PropertyToID("_SwarmSnow");
        static readonly int FadeId = Shader.PropertyToID("_SwarmFade");
        static readonly int FadeRadiusId = Shader.PropertyToID("_SwarmFadeRadius");
        static readonly int PropFadeId = Shader.PropertyToID("_SwarmPropFade");
        static readonly int PropFadeRadiusId = Shader.PropertyToID("_SwarmPropFadeRadius");

        /// <summary>巨型实体的拾取圆柱：xz 中心、地面高度、半径（树冠）、高度。</summary>
        struct GiantBounds
        {
            public Vector2 Center;
            public float Ground, Radius, Height;
        }

        readonly System.Collections.Generic.List<GiantBounds> _giants = new System.Collections.Generic.List<GiantBounds>();
        Vector3 _propFadeCenter;
        float _propFadeRadius = 4f;
        float _propFadeAmount;

        bool _cameraPlaced;
        TerrainOverlay _overlay;
        Vector3 _fadeCenter;
        float _fadeAmount;
        Camera _cam;

        EnvGrid _env;
        ResourceField _resources;
        GroundCoverField _cover;
        SeasonClock _season;
        TerrainMesher _terrain;
        GroundCoverRenderer _coverRenderer;
        ResourceRenderer _resourceRenderer;
        float _coverTimer;
        bool _subscribed;

        public EnvGrid Env => _env;
        public ResourceField Resources => _resources;
        public GroundCoverField Cover => _cover;
        public SeasonClock Season => _season;
        public GameBalance Balance => balance;
        public RegionDef Region => regions != null && regions.Length > 0 ? regions[regionIndex] : null;
        public uint Seed => seed;
        public ulong MapHash { get; private set; }
        public float LastGenerateMs { get; private set; }
        public float MountainPercent { get; private set; }
        public float CliffPercent { get; private set; }
        public float WaterPercent { get; private set; }
        public int SuperFertileTiles { get; private set; }

        /// <summary>每次世界（重新）生成完成后触发。</summary>
        public event System.Action Generated;

        void Awake()
        {
            Instance = this;
            EnsureContent();
            if (GetComponent<Swarm.Fields.PheromoneController>() == null)
                gameObject.AddComponent<Swarm.Fields.PheromoneController>();
        }

        /// <summary>鼠标射线与地形求交（笔刷、放置建筑、悬停虚化共用）。</summary>
        public bool PickTerrain(Ray ray, out Vector3 hit) => TryPickTerrain(ray, out hit);

        void Start()
        {
            Generate();
            TrySubscribe();
        }

        void OnDestroy()
        {
            if (_subscribed && GameLoop.Instance != null) GameLoop.Instance.OnTick1 -= Tick1;
            DisposeWorld();
            _resourceRenderer?.Dispose();
            if (Instance == this) Instance = null;
        }

        void TrySubscribe()
        {
            if (_subscribed || GameLoop.Instance == null) return;
            GameLoop.Instance.OnTick1 += Tick1;
            _subscribed = true;
        }

        /// <summary>缺少资产时（例如直接把组件拖进空场景）用 GDD 默认值。</summary>
        void EnsureContent()
        {
            if (balance != null && regions != null && regions.Length > 0 && resourceDefs != null && coverDefs != null) return;
            var d = DefaultContent.Create();
            if (balance == null) balance = d.balance;
            if (regions == null || regions.Length == 0) regions = d.regions;
            if (resourceDefs == null || resourceDefs.Length == 0) resourceDefs = d.resources;
            if (coverDefs == null || coverDefs.Length == 0) coverDefs = d.covers;
            Debug.LogWarning("[Swarm] WorldController is missing config assets; using built-in GDD defaults.", this);
        }

        public void Generate()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            DisposeWorld();
            regionIndex = Mathf.Clamp(regionIndex, 0, regions.Length - 1);
            var region = regions[regionIndex];

            _env = new EnvGrid(balance.mapSize);
            WorldGen.Generate(_env, region, balance, seed);

            _season = new SeasonClock(balance.yearLength, region.winterFraction, balance.seasonAmplitude);
            ApplySeasonTemperature();

            _cover = new GroundCoverField(_env, region, coverDefs, balance);
            _cover.Initialize(_env, seed, balance);

            _resources = new ResourceField();
            _resources.Generate(_env, region, balance, resourceDefs, seed);

            MapHash = WorldHash.Compute(_env, _resources);
            int obstacles = 0, cliffs = 0, water = 0;
            SuperFertileTiles = 0;
            for (int i = 0; i < _env.Length; i++)
            {
                if (_env.Fertility[i] > 1f) SuperFertileTiles++;
                if (_env.IsObstacle(i)) obstacles++;
                else if (_env.IsWater(i)) water++;
                else if (_env.IsCliff(i)) cliffs++;
            }
            MountainPercent = 100f * obstacles / _env.Length;
            CliffPercent = 100f * cliffs / _env.Length;
            WaterPercent = 100f * water / _env.Length;

            _terrain = new TerrainMesher(transform, terrainMaterial != null ? terrainMaterial : FallbackMaterial("Swarm/Terrain"));
            _terrain.Overlay = _overlay;
            _terrain.BuildAll(_env, balance, region);
            _coverRenderer = new GroundCoverRenderer(transform,
                groundCoverMaterial != null ? groundCoverMaterial : FallbackMaterial("Swarm/GroundCover"));
            _coverRenderer.Rebuild(_env, balance, _cover, true);

            _resourceRenderer?.Dispose();
            _resourceRenderer = new ResourceRenderer(resourceDefs,
                resourceMaterial != null ? resourceMaterial : FallbackMaterial("Swarm/Prop"));

            _coverTimer = 0f;
            CacheGiants();
            var rts = FindFirstObjectByType<RtsCamera>();
            if (rts != null)
            {
                rts.SetBounds(Vector2.zero, new Vector2(_env.Size, _env.Size));
                if (!_cameraPlaced) { rts.JumpTo(new Vector2(_env.Size * 0.5f, _env.Size * 0.5f)); _cameraPlaced = true; }
            }
            LastGenerateMs = (float)sw.Elapsed.TotalMilliseconds;
            Debug.Log($"[Swarm] World generated: region={region.id} seed={seed} hash={MapHash:X16} " +
                      $"nodes={_resources.Nodes.Length} giants={_resources.CountGiant} " +
                      $"superFertile={SuperFertileTiles} mountain={MountainPercent:0.0}% cliff={CliffPercent:0.0}% water={WaterPercent:0.0}% in {LastGenerateMs:0} ms");
            Generated?.Invoke();
        }

        static Material FallbackMaterial(string shaderName)
        {
            var shader = Shader.Find(shaderName);
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
            return new Material(shader);
        }

        void DisposeWorld()
        {
            _terrain?.Dispose(); _terrain = null;
            _coverRenderer?.Dispose(); _coverRenderer = null;
            _cover?.Dispose(); _cover = null;
            _resources?.Dispose(); _resources = null;
            _env?.Dispose(); _env = null;
        }

        void ApplySeasonTemperature()
        {
            float offset = _season.TemperatureOffset;
            for (int i = 0; i < _env.Length; i++)
                _env.Temperature[i] = _env.TemperatureBase[i] + offset;

            float regionTemp = Region.baseTemperature + offset;
            Shader.SetGlobalFloat(SnowId, Mathf.Clamp01((1f - regionTemp) / 6f));
        }

        void Tick1(float dt)
        {
            if (_env == null) return;
            bool seasonChanged = _season.Advance(dt);
            ApplySeasonTemperature();
            if (seasonChanged) _resources.OnSeasonChanged(_season.Current);
            _resources.Tick(_env, balance, _season.Current, dt);

            _coverTimer += dt;
            if (_coverTimer >= balance.coverIntervalSeconds - 1e-4f)
            {
                _coverTimer = 0f;
                _cover.Step(_env, balance);
                _coverRenderer.Rebuild(_env, balance, _cover, false);
            }
        }

        void Update()
        {
            TrySubscribe();
            HandleDebugInput();
            if (_env == null) return;
            UpdateHoverFade();
            _resourceRenderer?.Draw(_resources, _env, balance);
        }

        void CacheGiants()
        {
            _giants.Clear();
            for (int n = 0; n < _resources.Nodes.Length; n++)
            {
                var node = _resources.Nodes[n];
                if (node.Footprint <= 1) continue;
                var def = resourceDefs[node.Def];
                float s = node.Scale > 0f ? node.Scale : 1f;
                _giants.Add(new GiantBounds
                {
                    Center = new Vector2(node.Position.x, node.Position.y),
                    Ground = TerrainHeights.Sample(_env, balance, node.Position),
                    Radius = Mathf.Max(def.size.x, def.size.z) * s * 0.5f,
                    Height = def.size.y * s,
                });
            }
        }

        /// <summary>
        /// 鼠标悬停虚化：射线先与巨型实体的圆柱求交，再与地形高度场求交，取较近者。
        /// 命中巨型实体 → 该实体（及紧挨着的）虚化；命中高山或山脚 1 Tile 内 → 光标附近山体虚化。
        /// </summary>
        void UpdateHoverFade()
        {
            if (_cam == null) _cam = Camera.main;
            float mountainTarget = 0f, propTarget = 0f;
            var mouse = Mouse.current;
            if (_cam != null && mouse != null)
            {
                var ray = _cam.ScreenPointToRay(mouse.position.ReadValue());
                bool terrainHit = TryPickTerrain(ray, out var hit);
                float terrainT = terrainHit ? Vector3.Distance(ray.origin, hit) : float.MaxValue;
                int giant = PickGiant(ray, out float giantT);

                if (giant >= 0 && giantT <= terrainT + 0.01f)
                {
                    var g = _giants[giant];
                    propTarget = 1f;
                    _propFadeCenter = new Vector3(g.Center.x, g.Ground, g.Center.y);
                    _propFadeRadius = g.Radius;
                }
                else if (terrainHit)
                {
                    int x = Mathf.Clamp((int)hit.x, 0, _env.Size - 1), y = Mathf.Clamp((int)hit.z, 0, _env.Size - 1);
                    if (NearMountain(x, y))
                    {
                        mountainTarget = 1f;
                        _fadeCenter = _fadeAmount < 0.01f ? hit : Vector3.Lerp(_fadeCenter, hit, 1f - Mathf.Exp(-20f * Time.unscaledDeltaTime));
                    }
                }
            }
            float k = Time.unscaledDeltaTime * 4f;
            _fadeAmount = Mathf.MoveTowards(_fadeAmount, mountainTarget, k);
            _propFadeAmount = Mathf.MoveTowards(_propFadeAmount, propTarget, k);
            Shader.SetGlobalVector(FadeId, new Vector4(_fadeCenter.x, _fadeCenter.y, _fadeCenter.z, _fadeAmount));
            Shader.SetGlobalFloat(FadeRadiusId, balance.mountainFadeRadius);
            Shader.SetGlobalVector(PropFadeId, new Vector4(_propFadeCenter.x, _propFadeCenter.y, _propFadeCenter.z, _propFadeAmount));
            Shader.SetGlobalFloat(PropFadeRadiusId, _propFadeRadius);
        }

        /// <summary>射线与竖直圆柱（侧面 + 顶面）求交，返回最近的巨型实体索引。</summary>
        int PickGiant(Ray ray, out float bestT)
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

        bool NearMountain(int x, int y)
        {
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
                if (_env.InBounds(x + dx, y + dy) && _env.IsObstacle(_env.Index(x + dx, y + dy))) return true;
            return false;
        }

        /// <summary>沿射线步进求与地形的交点（先粗后细）。</summary>
        bool TryPickTerrain(Ray ray, out Vector3 hit)
        {
            hit = default;
            const float step = 1f, maxDist = 700f;
            float prevT = 0f;
            for (float t = 0f; t < maxDist; t += step)
            {
                Vector3 p = ray.GetPoint(t);
                if (p.x < 0 || p.z < 0 || p.x >= _env.Size || p.z >= _env.Size) { prevT = t; continue; }
                float h = TerrainHeights.Sample(_env, balance, new Unity.Mathematics.float2(p.x, p.z));
                if (p.y > h) { prevT = t; continue; }
                float lo = prevT, hi = t;
                for (int k = 0; k < 8; k++)
                {
                    float mid = (lo + hi) * 0.5f;
                    Vector3 m = ray.GetPoint(mid);
                    float hm = TerrainHeights.Sample(_env, balance, new Unity.Mathematics.float2(m.x, m.z));
                    if (m.y > hm) lo = mid; else hi = mid;
                }
                hit = ray.GetPoint(hi);
                return true;
            }
            return false;
        }

        void HandleDebugInput()
        {
            var kb = Keyboard.current;
            if (kb == null) return;
            if (kb.tabKey.wasPressedThisFrame)
            {
                regionIndex = (regionIndex + 1) % regions.Length;
                Generate();
            }
            if (kb.nKey.wasPressedThisFrame)
            {
                seed = (uint)Random.Range(1, int.MaxValue);
                Generate();
            }
            if (kb.rKey.wasPressedThisFrame) Generate();
            if (kb.mKey.wasPressedThisFrame && _terrain != null)
            {
                _overlay = (TerrainOverlay)(((int)_overlay + 1) % 3);
                _terrain.Overlay = _overlay;
                _terrain.BuildAll(_env, balance, Region);
            }
            if (kb.hKey.wasPressedThisFrame && _resources != null) _resources.DepleteAll();
            if (kb.rightBracketKey.wasPressedThisFrame && _season != null)
            {
                _season.SkipToNextSeason();
                ApplySeasonTemperature();
                _resources.OnSeasonChanged(_season.Current);
            }
        }

        public string DebugText()
        {
            if (_env == null) return "World: -";
            var r = Region;
            string cover = "";
            for (int t = 0; t < coverDefs.Length; t++)
                cover += $"{coverDefs[t].id} {_cover.VisibleTiles[t]}  ";
            return
                $"Region {r.id}  seed {seed}  hash {MapHash:X16}  gen {LastGenerateMs:0}ms\n" +
                $"Year {_season.Year + 1} {_season.Current} {_season.SeasonProgress * 100f:0}%   " +
                $"season dT {_season.TemperatureOffset:+0.0;-0.0}C   region T {r.baseTemperature + _season.TemperatureOffset:0.0}C\n" +
                $"Terrain  mountain {MountainPercent:0}%  cliff {CliffPercent:0}%  water {WaterPercent:0}%   giants {_resources.CountGiant}   super-fertile tiles {SuperFertileTiles}\n" +
                $"Nodes {_resources.Nodes.Length}  full {_resources.CountFull}  regrowing {_resources.CountRegrowing}  " +
                $"waiting {_resources.CountWaiting}  dead {_resources.CountDead}\n" +
                $"Cover tiles  {cover}\n" +
                "Tab region   N new seed   R regen   H harvest all   ] next season   M overlay: " + _overlay;
        }
    }
}
