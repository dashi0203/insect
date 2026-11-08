using System;
using System.Collections.Generic;
using Swarm.Core;
using Swarm.Data;
using Swarm.World;
using Unity.Mathematics;

namespace Swarm.Buildings
{
    public enum BuildingType : byte
    {
        NestCore,      // 巢心：地表入口 + 虫母室，覆盖 24
        NestEntrance,  // 巢口：新的出入口，归巢场起点（M4）
        PheromoneNode, // 信息素节点：覆盖 12
        Outpost,       // 哨所：覆盖 16（M7）
        SporeSprayer,  // 孢子喷射器（M7）
        EggCase,       // 预制卵鞘（地下，M8）
    }

    public struct Building
    {
        public BuildingType Type;
        public int2 Tile;
        public float Health;
        public float CoverageRadius;
    }

    /// <summary>
    /// 建筑数据（GDD 10）。M2 只实现巢心（开局自动选址）和信息素节点（调试放置，用于验证覆盖链式扩张）。
    /// 纯数据：外观由渲染层的 BuildingRenderer 按 Buildings 列表生成。
    /// </summary>
    public sealed class BuildingSystem
    {
        readonly List<Building> _buildings = new List<Building>();

        public IReadOnlyList<Building> Buildings => _buildings;
        /// <summary>建筑增减时触发。</summary>
        public event Action Changed;

        public static float CoverageRadiusFor(BuildingType t, GameBalance b) => t switch
        {
            BuildingType.NestCore => b.nestCoreCoverageRadius,
            BuildingType.PheromoneNode => b.pheromoneNodeCoverageRadius,
            BuildingType.Outpost => b.outpostCoverageRadius,
            _ => 0f,
        };

        public static float MaxHealthFor(BuildingType t) => t switch
        {
            BuildingType.NestCore => 800f,
            BuildingType.NestEntrance => 150f,
            BuildingType.PheromoneNode => 80f,
            BuildingType.Outpost => 300f,
            BuildingType.SporeSprayer => 200f,
            _ => 150f,
        };

        /// <summary>归巢场的起点：巢心与所有巢口。</summary>
        public IEnumerable<int2> Entrances()
        {
            foreach (var bld in _buildings)
                if (bld.Type == BuildingType.NestCore || bld.Type == BuildingType.NestEntrance)
                    yield return bld.Tile;
        }

        public bool TryGetNestCore(out Building core)
        {
            foreach (var bld in _buildings)
                if (bld.Type == BuildingType.NestCore) { core = bld; return true; }
            core = default;
            return false;
        }

        /// <summary>
        /// 开局巢心选址：平地、7 × 7 内全部可走且无坡道 / 悬崖 / 巨型实体，离高山 ≥ 5 格、离地图边 ≥ 40 格；
        /// 偏好靠近地图中心、肥沃的地方。同一种子结果相同。
        /// </summary>
        public Building PlaceNestCore(EnvGrid env, GameBalance b, uint seed)
        {
            var rng = SimRandom.Create(seed, RandomStreams.Colony);
            float bestScore = float.MinValue;
            int2 best = new int2(env.Size / 2, env.Size / 2);
            int margin = math.min(40, env.Size / 4);
            float2 center = new float2(env.Size * 0.5f);
            for (int y = margin; y < env.Size - margin; y += 3)
            for (int x = margin; x < env.Size - margin; x += 3)
            {
                if (!SiteOk(env, x, y)) continue;
                float dist = math.distance(new float2(x, y), center) / env.Size;
                float score = -dist + 0.25f * math.min(1f, env.Fertility[env.Index(x, y)]) + rng.NextFloat() * 0.08f;
                if (score > bestScore) { bestScore = score; best = new int2(x, y); }
            }
            var core = Add(BuildingType.NestCore, best, b);
            return core;
        }

        static bool SiteOk(EnvGrid env, int x, int y)
        {
            for (int dy = -5; dy <= 5; dy++)
            for (int dx = -5; dx <= 5; dx++)
            {
                int tx = x + dx, ty = y + dy;
                if (!env.InBounds(tx, ty)) return false;
                int i = env.Index(tx, ty);
                if (env.IsObstacle(i)) return false;
                if (math.abs(dx) > 3 || math.abs(dy) > 3) continue;
                if (!env.IsGroundWalkable(i) || env.IsRamp(i) || env.HeightLevel[i] != 0) return false;
            }
            return true;
        }

        /// <summary>
        /// 放置地表建筑：必须在覆盖内、可走、不在坡道上，且离其他建筑 ≥ 3 格（GDD 10：地表建筑只能放在覆盖范围内）。
        /// </summary>
        public bool TryPlace(BuildingType type, int2 tile, EnvGrid env, Swarm.Fields.CoverageMap coverage, GameBalance b)
        {
            if (!env.InBounds(tile)) return false;
            int i = env.Index(tile);
            if (!coverage.IsCovered(tile.x, tile.y) || !env.IsGroundWalkable(i) || env.IsRamp(i)) return false;
            foreach (var other in _buildings)
                if (math.distancesq(other.Tile, tile) < 9) return false;
            Add(type, tile, b);
            return true;
        }

        Building Add(BuildingType type, int2 tile, GameBalance b)
        {
            var bld = new Building
            {
                Type = type,
                Tile = tile,
                Health = MaxHealthFor(type),
                CoverageRadius = CoverageRadiusFor(type, b),
            };
            _buildings.Add(bld);
            Changed?.Invoke();
            return bld;
        }
    }
}
