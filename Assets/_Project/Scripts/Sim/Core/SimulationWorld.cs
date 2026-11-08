using System;
using System.Collections.Generic;
using System.Diagnostics;
using Swarm.Agents;
using Swarm.Buildings;
using Swarm.Colony;
using Swarm.Data;
using Swarm.Fields;
using Swarm.World;
using Unity.Mathematics;
using Random = Unity.Mathematics.Random;

namespace Swarm.Core
{
    /// <summary>各系统的随机数通道 id。新增系统时在这里加一项，不要改已有的值。</summary>
    public static class RandomStreams
    {
        public const uint WorldGen = 1;
        public const uint Ecology = 2;
        public const uint Agents = 3;
        public const uint Colony = 4;
        public const uint Humans = 5;
        public const uint Resources = 6;
        public const uint GroundCover = 7;
    }

    public static class SimRandom
    {
        /// <summary>为某个系统派生独立的随机数生成器（GDD 14.1）。同一种子 + 同一 stream 永远得到同一序列。</summary>
        public static Random Create(uint seed, uint stream)
        {
            if (seed == 0) seed = 1;
            uint s = math.hash(new uint2(seed, stream));
            return new Random(s == 0 ? 0x9E3779B9u : s);
        }
    }

    /// <summary>一局用到的全部配置资产（GDD 14.5）。</summary>
    public sealed class SimContent
    {
        public GameBalance Balance;
        public RegionDef[] Regions;
        public ResourceNodeDef[] Resources;
        public GroundCoverDef[] Covers;

        public bool IsComplete => Balance != null && Regions != null && Regions.Length > 0
                                  && Resources != null && Resources.Length > 0 && Covers != null && Covers.Length > 0;

        public static SimContent Defaults()
        {
            var d = DefaultContent.Create();
            return new SimContent { Balance = d.balance, Regions = d.regions, Resources = d.resources, Covers = d.covers };
        }
    }

    /// <summary>模拟系统：只读写 SimulationWorld 中的数据，不碰 GameObject、输入或渲染。</summary>
    public interface ISimSystem
    {
        string Name { get; }
        void Tick(SimulationWorld world, float dt);
    }

    /// <summary>生成时统计的世界概况（调试用）。</summary>
    public struct WorldStats
    {
        public ulong MapHash;
        public float GenerateMs;
        public float MountainPercent, CliffPercent, WaterPercent;
        public int SuperFertileTiles;
    }

    /// <summary>
    /// 模拟世界的根（GDD 14）：持有全部模拟状态，按固定顺序执行各节拍的系统。
    /// 不依赖任何 MonoBehaviour；渲染层只读这里的数据，通过公开方法（笔刷、放置建筑）下达玩家意图。
    /// 同一步内顺序固定为 10 Hz → 5 Hz → 1 Hz，同一节拍内按注册顺序执行。
    /// </summary>
    public sealed class SimulationWorld : IDisposable
    {
        public uint Seed { get; }
        public SimContent Content { get; }
        public GameBalance Balance => Content.Balance;
        public int RegionIndex { get; }
        public RegionDef Region => Content.Regions[RegionIndex];

        public EnvGrid Env { get; private set; }
        public SeasonClock Season { get; private set; }
        public ResourceField Resources { get; private set; }
        public GroundCoverField Cover { get; private set; }
        public BuildingSystem Buildings { get; private set; }
        public CoverageMap Coverage { get; private set; }
        public PheromoneFields Pheromones { get; private set; }
        /// <summary>按行为档案预合成的转向场，5 Hz 随信息素场重建。</summary>
        public SteeringField Steering { get; private set; }
        /// <summary>每格可采集实体索引，1 Hz 随资源结算重建。</summary>
        public ResourceLookup Lookup { get; private set; }
        public AgentStore Agents { get; private set; }
        public ColonyStore Colony { get; private set; }
        public WorldStats Stats { get; private set; }

        /// <summary>模拟时间（秒）。</summary>
        public double Time { get; private set; }

        /// <summary>季节变化（参数为新季节）。</summary>
        public event Action<Season> SeasonChanged;
        /// <summary>地被元胞自动机跑完一步，渲染据此重建脏分块。</summary>
        public event Action CoverStepped;
        /// <summary>建筑增减，覆盖与归巢场已重算。</summary>
        public event Action BuildingsChanged;

        readonly List<ISimSystem> _hz10 = new List<ISimSystem>();
        readonly List<ISimSystem> _hz5 = new List<ISimSystem>();
        readonly List<ISimSystem> _hz1 = new List<ISimSystem>();
        readonly Dictionary<string, float> _systemMs = new Dictionary<string, float>();
        readonly List<IDisposable> _ownedSystems = new List<IDisposable>();

        public IReadOnlyDictionary<string, float> SystemMs => _systemMs;

        SimulationWorld(SimContent content, int regionIndex, uint seed)
        {
            Content = content;
            RegionIndex = math.clamp(regionIndex, 0, content.Regions.Length - 1);
            Seed = seed == 0 ? 1u : seed;
        }

        /// <summary>生成一整个世界：环境、地被、资源、开局巢心、覆盖、信息素场。同一配置 + 种子 → 同一结果。</summary>
        public static SimulationWorld Create(SimContent content, int regionIndex, uint seed)
        {
            if (content == null || !content.IsComplete) throw new ArgumentException("SimContent is incomplete.", nameof(content));
            var w = new SimulationWorld(content, regionIndex, seed);
            w.Build();
            return w;
        }

        void Build()
        {
            var sw = Stopwatch.StartNew();
            var b = Balance;
            var region = Region;

            Env = new EnvGrid(b.mapSize);
            WorldGen.Generate(Env, region, b, Seed);

            Season = new SeasonClock(b.yearLength, region.winterFraction, b.seasonAmplitude);
            ApplySeasonTemperature();

            Cover = new GroundCoverField(Env, region, Content.Covers, b);
            Cover.Initialize(Env, Seed, b);

            Resources = new ResourceField();
            Resources.Generate(Env, region, b, Content.Resources, Seed);

            Buildings = new BuildingSystem();
            Buildings.PlaceNestCore(Env, b, Seed);
            Coverage = new CoverageMap(Env.Size);
            Pheromones = new PheromoneFields(Env, b);
            Steering = new SteeringField(Env.Size, b);
            RefreshBuildingDerived();
            Pheromones.RecomputeEnvironment(Env, Resources);
            Buildings.Changed += OnBuildingsChanged;

            Lookup = new ResourceLookup(Env.Size, Content.Resources);
            Lookup.Build(Resources, b.harvestRange);
            Colony = new ColonyStore(b);
            Agents = new AgentStore(Env.Size);
            if (Buildings.TryGetNestCore(out var core))
                Agents.SpawnWorkers(Env, b, b.startingWorkers, new float2(core.Tile) + 0.5f, 6f, b.startingMediumFraction, Seed);

            // 节拍系统，顺序即执行顺序
            var agents = new AgentSystem(this);
            _ownedSystems.Add(agents);
            _hz10.Add(agents);
            _hz5.Add(new PheromoneFieldSystem());
            _hz5.Add(new SteeringFieldSystem());
            _hz1.Add(new SeasonSystem());
            _hz1.Add(new ResourceRegrowthSystem());
            _hz1.Add(new ResourceLookupSystem());
            _hz1.Add(new GroundCoverSystem());
            _hz1.Add(new EnvironmentScentSystem());
            _hz1.Add(new EconomySystem());

            Stats = ComputeStats((float)sw.Elapsed.TotalMilliseconds);
        }

        WorldStats ComputeStats(float ms)
        {
            int obstacles = 0, cliffs = 0, water = 0, super = 0;
            for (int i = 0; i < Env.Length; i++)
            {
                if (Env.Fertility[i] > 1f) super++;
                if (Env.IsObstacle(i)) obstacles++;
                else if (Env.IsWater(i)) water++;
                else if (Env.IsCliff(i)) cliffs++;
            }
            return new WorldStats
            {
                MapHash = WorldHash.Compute(Env, Resources),
                GenerateMs = ms,
                MountainPercent = 100f * obstacles / Env.Length,
                CliffPercent = 100f * cliffs / Env.Length,
                WaterPercent = 100f * water / Env.Length,
                SuperFertileTiles = super,
            };
        }

        // ---------------------------------------------------------------- stepping

        /// <summary>执行一个基础步（10 Hz），flags 表示本步同时触发的 5 Hz / 1 Hz 节拍。</summary>
        public void Step(TickFlags flags)
        {
            Time += TickClock.Dt10;
            Run(_hz10, TickClock.Dt10);
            if ((flags & TickFlags.Hz5) != 0) Run(_hz5, TickClock.Dt5);
            if ((flags & TickFlags.Hz1) != 0) Run(_hz1, TickClock.Dt1);
        }

        void Run(List<ISimSystem> systems, float dt)
        {
            for (int i = 0; i < systems.Count; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                systems[i].Tick(this, dt);
                _systemMs[systems[i].Name] = (float)((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
            }
        }

        internal void ApplySeasonTemperature()
        {
            float offset = Season.TemperatureOffset;
            var baseT = Env.TemperatureBase;
            var t = Env.Temperature;
            for (int i = 0; i < Env.Length; i++) t[i] = baseT[i] + offset;
        }

        internal void RaiseSeasonChanged() => SeasonChanged?.Invoke(Season.Current);
        internal void RaiseCoverStepped() => CoverStepped?.Invoke();

        // ---------------------------------------------------------------- player intents

        /// <summary>在覆盖范围内绘制玩家信息素（GDD 4.3）。返回写入的 Tile 数。</summary>
        public int PaintPheromone(PheromoneChannel channel, float2 center, float radius, float strength) =>
            Pheromones.Paint(channel, center, math.clamp(radius, Balance.brushRadiusMin, Balance.brushRadiusMax),
                math.clamp(strength, Balance.brushStrengthMin, Balance.brushStrengthMax), Coverage.Covered);

        public void ErasePheromone(float2 center, float radius) =>
            Pheromones.Erase(center, math.clamp(radius, Balance.brushRadiusMin, Balance.brushRadiusMax));

        /// <summary>放置地表建筑（必须在覆盖内，GDD 10）。</summary>
        public bool TryPlaceBuilding(BuildingType type, int2 tile) => Buildings.TryPlace(type, tile, Env, Coverage, Balance);

        void OnBuildingsChanged()
        {
            RefreshBuildingDerived();
            BuildingsChanged?.Invoke();
        }

        void RefreshBuildingDerived()
        {
            Coverage.Recompute(Buildings.Buildings);
            Pheromones.RecomputeHoming(Buildings.Entrances());
            Steering.Build(Pheromones, Balance.gradientSaturation);
        }

        /// <summary>调试 / 性能测试：在巢心附近生成工虫。返回实际生成数量。</summary>
        public int SpawnWorkers(int count)
        {
            if (!Buildings.TryGetNestCore(out var core)) return 0;
            return Agents.SpawnWorkers(Env, Balance, count, new float2(core.Tile) + 0.5f, 10f, Balance.startingMediumFraction, Seed);
        }

        // ---------------------------------------------------------------- debug

        public void DebugDepleteAllResources() => Resources.DepleteAll();

        public void DebugSkipSeason()
        {
            Season.SkipToNextSeason();
            ApplySeasonTemperature();
            Resources.OnSeasonChanged(Season.Current);
            RaiseSeasonChanged();
        }

        public void Dispose()
        {
            if (Buildings != null) Buildings.Changed -= OnBuildingsChanged;
            foreach (var sys in _ownedSystems) sys.Dispose();
            _ownedSystems.Clear();
            Agents?.Dispose(); Agents = null;
            Colony?.Dispose(); Colony = null;
            Lookup?.Dispose(); Lookup = null;
            Steering?.Dispose(); Steering = null;
            Pheromones?.Dispose(); Pheromones = null;
            Coverage?.Dispose(); Coverage = null;
            Cover?.Dispose(); Cover = null;
            Resources?.Dispose(); Resources = null;
            Env?.Dispose(); Env = null;
        }
    }
}
