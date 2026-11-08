using Swarm.Agents;
using Unity.Mathematics;

namespace Swarm.Core
{
    // 节拍系统。每个系统只做一件事；执行顺序由 SimulationWorld 注册顺序决定。

    /// <summary>1 Hz：季节推进与本地温度（GDD 2.5）。</summary>
    public sealed class SeasonSystem : ISimSystem
    {
        public string Name => "Season";

        public void Tick(SimulationWorld w, float dt)
        {
            bool changed = w.Season.Advance(dt);
            w.ApplySeasonTemperature();
            if (!changed) return;
            w.Resources.OnSeasonChanged(w.Season.Current);
            w.RaiseSeasonChanged();
        }
    }

    /// <summary>1 Hz：资源实体再生、毒性枯死、重新播种（GDD 2.6）。</summary>
    public sealed class ResourceRegrowthSystem : ISimSystem
    {
        public string Name => "Resources";
        public void Tick(SimulationWorld w, float dt) => w.Resources.Tick(w.Env, w.Balance, w.Season.Current, dt);
    }

    /// <summary>每 coverIntervalSeconds 一次地被元胞自动机（GDD 2.7）。</summary>
    public sealed class GroundCoverSystem : ISimSystem
    {
        float _timer;
        public string Name => "GroundCover";

        public void Tick(SimulationWorld w, float dt)
        {
            _timer += dt;
            if (_timer < w.Balance.coverIntervalSeconds - 1e-4f) return;
            _timer = 0f;
            w.Cover.Step(w.Env, w.Balance);
            w.RaiseCoverStepped();
        }
    }

    /// <summary>1 Hz：资源气味与危险场重算（GDD 4.1）。</summary>
    public sealed class EnvironmentScentSystem : ISimSystem
    {
        public string Name => "Scent";
        public void Tick(SimulationWorld w, float dt) => w.Pheromones.RecomputeEnvironment(w.Env, w.Resources);
    }

    /// <summary>5 Hz：信息素场扩散、衰减、向绘制层靠拢（GDD 4.2）。</summary>
    public sealed class PheromoneFieldSystem : ISimSystem
    {
        public string Name => "Pheromones";
        public void Tick(SimulationWorld w, float dt) => w.Pheromones.Step(dt, w.Coverage.Covered);
    }

    /// <summary>5 Hz：信息素场更新后重建预合成转向场。</summary>
    public sealed class SteeringFieldSystem : ISimSystem
    {
        public string Name => "Steering";
        public void Tick(SimulationWorld w, float dt) => w.Steering.Build(w.Pheromones, w.Balance.gradientSaturation);
    }

    /// <summary>1 Hz：资源结算后重建每格可采集实体索引。</summary>
    public sealed class ResourceLookupSystem : ISimSystem
    {
        public string Name => "Lookup";
        public void Tick(SimulationWorld w, float dt) => w.Lookup.Build(w.Resources, w.Balance.harvestRange);
    }

    /// <summary>1 Hz：库存历史与工虫选资源权重（GDD 7.2 / 7.4）。</summary>
    public sealed class EconomySystem : ISimSystem
    {
        public string Name => "Economy";

        public void Tick(SimulationWorld w, float dt)
        {
            var tiers = w.Balance.sizeTiers;
            float upkeep = 0f;
            var agents = w.Agents.Agents;
            if (tiers != null && tiers.Length == 4)
                for (int i = 0; i < agents.Length; i++)
                    if (agents[i].IsAlive) upkeep += tiers[agents[i].Tier].upkeepPerMinute;
            w.Colony.Tick(upkeep, float3.zero); // 幼虫与待建需求：M4 / M5 接入
        }
    }
}
