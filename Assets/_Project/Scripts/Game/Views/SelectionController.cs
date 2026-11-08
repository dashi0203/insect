using System;
using System.Text;
using Swarm.Agents;
using Swarm.Core;
using Swarm.Data;
using Swarm.Fields;
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
    /// 选中单位（GDD 13：单位只能选中查看，不能下令）。没有拿笔刷时左键点一只虫子：
    /// 脚下画高亮圈，画一条线指向它此刻的目标（采集中的实体 / 巢心 / 转向方向），右下面板用中文说明它在做什么、为什么。
    /// Esc 或点空地取消选中。
    /// </summary>
    public sealed class SelectionController : IDisposable
    {
        const float PickRadius = 1.5f;
        static readonly string[] KindNames = { "蛋白质", "矿物质", "建筑质" };
        static readonly string[] TierNames = { "小型", "中型", "大型", "特大" };

        readonly LineRenderer _ring, _line;
        readonly Material _material;
        readonly StringBuilder _sb = new StringBuilder(512);
        SelectionPanel _panel;

        SimulationWorld _world;
        WorldPicker _picker;
        ResourceNodeDef[] _defs;
        int _selected = -1;

        public bool PointerOverPanel => _panel != null && _panel.PointerOver;

        public SelectionController(Transform parent)
        {
            var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Universal Render Pipeline/Unlit");
            _material = new Material(shader);
            _ring = MakeLine(parent, "SelectionRing", 32, true, 0.08f);
            _line = MakeLine(parent, "SelectionIntent", 2, false, 0.06f);
        }

        LineRenderer MakeLine(Transform parent, string name, int points, bool loop, float width)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.loop = loop;
            lr.positionCount = points;
            lr.widthMultiplier = width;
            lr.useWorldSpace = true;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            lr.sharedMaterial = _material;
            lr.enabled = false;
            return lr;
        }

        public void BuildPanel(UIDocument doc)
        {
            if (doc != null && _panel == null) _panel = new SelectionPanel(doc.rootVisualElement);
        }

        public void Bind(SimulationWorld world, WorldPicker picker, ResourceNodeDef[] defs)
        {
            _world = world;
            _picker = picker;
            _defs = defs;
            Deselect();
        }

        public void Deselect()
        {
            _selected = -1;
            _ring.enabled = _line.enabled = false;
            _panel?.Hide();
        }

        /// <param name="canPick">没有拿笔刷、指针不在 UI 上时为 true。</param>
        public void Update(Camera cam, bool canPick, float alpha)
        {
            if (_world == null) return;
            if (SwarmInput.Pressed(SwarmAction.BrushCancel)) Deselect();
            if (canPick && cam != null && SwarmInput.PrimaryPressed && _picker.PickTerrain(cam.ScreenPointToRay(SwarmInput.PointerPosition), out var hit))
                Pick(new float2(hit.x, hit.z));

            var agents = _world.Agents.Agents;
            if (_selected < 0 || _selected >= agents.Length || !agents[_selected].IsAlive)
            {
                if (_selected >= 0) Deselect();
                return;
            }
            var a = agents[_selected];
            float2 p = math.lerp(a.PrevPosition, a.Position, math.saturate(alpha));
            DrawMarkers(a, p);
            _panel?.Show($"工虫 #{_selected}　{TierNames[a.Tier]}", Describe(a));
        }

        void Pick(float2 at)
        {
            var agents = _world.Agents.Agents;
            int best = -1;
            float bestD = PickRadius * PickRadius;
            for (int i = 0; i < agents.Length; i++)
            {
                if (!agents[i].IsAlive || agents[i].Layer != 0) continue;
                float d = math.distancesq(agents[i].Position, at);
                if (d < bestD) { bestD = d; best = i; }
            }
            if (best < 0) Deselect();
            else _selected = best;
        }

        // ---------------------------------------------------------------- markers

        void DrawMarkers(in AgentData a, float2 p)
        {
            var env = _world.Env;
            var b = _world.Balance;
            float y = TerrainHeights.Sample(env, b, p) + 0.08f;
            float r = 0.55f * (b.sizeTiers != null && b.sizeTiers.Length == 4 ? b.sizeTiers[a.Tier].scale : 0.7f) + 0.25f;
            _ring.enabled = true;
            _ring.startColor = _ring.endColor = new Color(1f, 0.95f, 0.35f);
            for (int k = 0; k < _ring.positionCount; k++)
            {
                float ang = k / (float)_ring.positionCount * Mathf.PI * 2f;
                _ring.SetPosition(k, new Vector3(p.x + Mathf.Cos(ang) * r, y, p.y + Mathf.Sin(ang) * r));
            }

            float2 target;
            Color col;
            switch ((AgentState)a.State)
            {
                case AgentState.WorkerHarvest:
                    target = a.Target >= 0 ? _world.Resources.Nodes[a.Target].Position : p;
                    col = new Color(0.4f, 1f, 0.45f);
                    break;
                case AgentState.WorkerReturn:
                case AgentState.WorkerFlee:
                    target = _world.Buildings.TryGetNestCore(out var core) ? new float2(core.Tile) + 0.5f : p;
                    col = new Color(1f, 0.85f, 0.3f);
                    break;
                default:
                    int profile = a.ResourceKind;
                    float2 dir = SteeringField.Sample(_world.Steering.Field, env.Size, profile, a.Position);
                    target = p + math.normalizesafe(dir) * math.min(6f, 1.5f + math.length(dir) * 3f);
                    col = new Color(0.55f, 0.85f, 1f);
                    break;
            }
            _line.enabled = math.distancesq(target, p) > 0.01f;
            _line.startColor = _line.endColor = col;
            _line.SetPosition(0, new Vector3(p.x, y + 0.1f, p.y));
            _line.SetPosition(1, new Vector3(target.x, TerrainHeights.Sample(env, b, target) + 0.18f, target.y));
        }

        // ---------------------------------------------------------------- text

        string Describe(in AgentData a)
        {
            var w = _world;
            var env = w.Env;
            var b = w.Balance;
            int tile = ResourceField.TileIndex(env, a.Position);
            int homing = w.Pheromones.HomingDistance[tile];
            bool covered = w.Coverage.Covered[tile] != 0;
            var tier = b.sizeTiers[a.Tier];
            _sb.Clear();

            switch ((AgentState)a.State)
            {
                case AgentState.WorkerSearch:
                {
                    string kind = KindNames[a.ResourceKind];
                    _sb.Append("状态：搜索　要找的资源：").Append(kind).Append('\n');
                    _sb.Append("意图：").Append(SearchIntent(a, tile, covered, kind)).Append('\n');
                    float left = math.max(0f, b.searchGiveUpSeconds - a.StateTimer);
                    _sb.Append($"再找 {left:0} 秒还没找到就换一种资源\n");
                    break;
                }
                case AgentState.WorkerHarvest:
                {
                    var node = w.Resources.Nodes[a.Target];
                    var def = _defs[node.Def];
                    float duration = math.max(b.harvestSeconds, tier.carry / math.max(0.01f, def.harvestRatePerSecond));
                    string amount = node.Infinite != 0 ? "无限" : $"{node.Amount:0}";
                    _sb.Append("状态：采集\n");
                    _sb.Append($"意图：正在采集 {def.displayName}（{KindNames[(int)def.kind]}，剩余 {amount}）\n");
                    _sb.Append($"进度 {math.min(a.StateTimer, duration):0.0} / {duration:0.0} 秒，采完背回巢\n");
                    break;
                }
                case AgentState.WorkerReturn:
                    _sb.Append("状态：返巢\n");
                    _sb.Append(a.Carry > 0f
                        ? $"意图：背着 {a.Carry:0.0} {KindNames[a.CarryKind]} 回巢，沿途留下路径信息素\n"
                        : "意图：找了很久没找到资源，空手回巢，到了再重新选一种\n");
                    _sb.Append(homing == PheromoneFields.Unreachable ? "回巢路线：无法到达巢口\n" : $"离巢口还有 {homing} 格\n");
                    break;
                case AgentState.WorkerFlee:
                    _sb.Append("状态：逃跑\n意图：受伤且周围警报很高，沿归巢方向撤退\n");
                    break;
                default:
                    _sb.Append("状态：").Append(((AgentState)a.State).ToString()).Append('\n');
                    break;
            }

            if (w.GatherRegions.TryGetRegionAt(tile, out var region))
                _sb.Append($"所在采集区：{region.Crowd} / {region.Capacity:0} 只" +
                           (region.Factor <= 0f ? "（已满，不再吸引远处工虫）\n" : $"（还能吸引 {region.Factor * 100f:0}%）\n"));
            _sb.Append($"携带：{(a.Carry > 0f ? $"{a.Carry:0.0} {KindNames[a.CarryKind]}" : "无")}　　生命 {a.Health:0}/{tier.health:0}　　年龄 {a.Age:0} 秒\n");
            _sb.Append($"位置 ({a.Position.x:0}, {a.Position.y:0})　{(covered ? "覆盖范围内" : "覆盖范围外")}　速度 {math.length(a.Velocity):0.0} 格/秒");
            return _sb.ToString();
        }

        string SearchIntent(in AgentData a, int tile, bool covered, string kind)
        {
            var f = _world.Pheromones;
            float gatherHere = f.Get(PheromoneChannel.Gather, tile);
            float gatherFar = f.PaintReach[tile];
            float avoid = f.Get(PheromoneChannel.Avoid, tile);
            float scent = f.Get(Pheromones.ScentFor((ResourceKind)a.ResourceKind), tile);
            float trail = f.Get(PheromoneChannel.Trail, tile);

            if (!covered) return "在覆盖范围外，正被拉回巢附近";
            if (avoid > 0.2f) return "在回避信息素里，正在离开";
            if (gatherHere > _world.Balance.gatherOverrideThreshold) return "在采集信息素区域里：碰到任何资源都会采";
            if (gatherFar > 0.01f) return "感觉到远处还有空位的采集区，正往那边走";
            if (scent > 0.05f) return $"闻到{kind}的气味（{scent:0.00}），正在靠近";
            if (trail > 0.5f) return "沿着同伴留下的路径走";
            return $"附近没有线索，四处游荡寻找{kind}";
        }

        public void Dispose()
        {
            _panel?.Remove();
            if (_ring != null) Object.Destroy(_ring.gameObject);
            if (_line != null) Object.Destroy(_line.gameObject);
            if (_material != null) Object.Destroy(_material);
        }
    }
}
