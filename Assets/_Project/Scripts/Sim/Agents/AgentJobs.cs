using Swarm.Fields;
using Swarm.World;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Swarm.Agents
{
    /// <summary>Job 用的体型参数（从 SizeTierSettings 拷贝）。</summary>
    public struct TierParams
    {
        public float Speed, Carry, Health;
    }

    /// <summary>Job 用的行为参数（从 GameBalance 拷贝）。</summary>
    public struct BehaviourParams
    {
        public float HarvestRange, HarvestSeconds, GiveUpSeconds;
        public float Wander, WanderTurn, SeparationRadius, SeparationStrength, Response;
        public float ColdStop, ColdMultiplier;
        public float FleeHealth, FleeAlarm;
        public float GatherOverride;
        public int UnloadDistance;
    }

    /// <summary>空间哈希：按 2 × 2 Tile 的格子做计数排序（单线程，O(n + 格子数)）。</summary>
    [BurstCompile(CompileSynchronously = true)]
    struct BuildHashJob : IJob
    {
        public int cellsPerSide, cellSize;
        [ReadOnly] public NativeArray<AgentData> agents;
        public NativeArray<int> cellStart; // 长度 cells + 1
        public NativeArray<int> sorted;

        public void Execute()
        {
            int cells = cellStart.Length - 1;
            for (int c = 0; c <= cells; c++) cellStart[c] = 0;
            for (int i = 0; i < agents.Length; i++) cellStart[Cell(agents[i].Position) + 1]++;
            for (int c = 0; c < cells; c++) cellStart[c + 1] += cellStart[c];
            // 用 sorted 的写指针复用 cellStart：先拷贝起点
            var cursor = new NativeArray<int>(cells, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            for (int c = 0; c < cells; c++) cursor[c] = cellStart[c];
            for (int i = 0; i < agents.Length; i++)
            {
                int c = Cell(agents[i].Position);
                sorted[cursor[c]++] = i;
            }
            cursor.Dispose();
        }

        int Cell(float2 p)
        {
            int x = math.clamp((int)(p.x / cellSize), 0, cellsPerSide - 1);
            int y = math.clamp((int)(p.y / cellSize), 0, cellsPerSide - 1);
            return y * cellsPerSide + x;
        }
    }

    /// <summary>
    /// 并行步（10 Hz）：感知 → 状态 → 转向 → 移动。每只虫子只读上一状态（cur），只写自己（next），
    /// 需要改共享数据的操作（采集、卸货、留路径、选资源）写成请求标志，交给 ResolveJob。
    /// </summary>
    [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Fast)]
    struct WorkerStepJob : IJobParallelFor
    {
        public float dt;
        public int size;
        public BehaviourParams p;
        [ReadOnly] public NativeArray<AgentData> cur;
        [WriteOnly] public NativeArray<AgentData> next;
        [ReadOnly] public NativeArray<TierParams> tiers;
        [ReadOnly] public NativeArray<float2> steering;
        [ReadOnly] public NativeArray<byte> open;
        [ReadOnly] public NativeArray<int> homing;
        [ReadOnly] public NativeArray<float> temperature;
        [ReadOnly] public NativeArray<float> alarm;  // 警报通道（当前场的切片）
        [ReadOnly] public NativeArray<float> gather; // 采集通道（当前场的切片）
        [ReadOnly] public NativeArray<ResourceNode> nodes;
        [ReadOnly] public NativeArray<NodeInfo> nodeInfo;
        [ReadOnly] public NativeArray<int> nodeAt;
        [ReadOnly] public NativeArray<int> cellStart;
        [ReadOnly] public NativeArray<int> sorted;
        public int cellsPerSide, cellSize;

        public void Execute(int i)
        {
            var a = cur[i];
            if (!a.IsAlive) { next[i] = a; return; }

            a.Flags = (byte)(a.Flags & ~(byte)AgentFlags.RequestMask);
            a.PrevPosition = a.Position;
            a.Age += dt;
            var t = tiers[a.Tier];
            uint rng = a.Rng;
            int tiles = size * size;
            int tile = Tile(a.Position);

            float2 desired = 0;
            bool moves = true;
            switch ((AgentState)a.State)
            {
                case AgentState.WorkerSearch:
                {
                    a.StateTimer += dt;
                    int n = nodeAt[a.ResourceKind * tiles + tile];
                    if (n < 0 || !ResourceLookup.Harvestable(nodes[n]))
                    {
                        n = -1;
                        // 玩家涂了采集信息素的地方：见到什么采什么（玩家意图优先于工虫自己的选择）
                        if (gather[tile] > p.GatherOverride)
                            for (int k = 0; k < 3 && n < 0; k++)
                            {
                                int c = nodeAt[k * tiles + tile];
                                if (c >= 0 && ResourceLookup.Harvestable(nodes[c])) n = c;
                            }
                    }
                    if (n >= 0)
                    {
                        a.State = (byte)AgentState.WorkerHarvest;
                        a.Target = n;
                        a.StateTimer = 0f;
                        moves = false;
                        break;
                    }
                    if (a.StateTimer > p.GiveUpSeconds)
                    {
                        // 找了很久没找到：空手回巢，卸货时重新选资源（不会困在台地或空涂抹区里）
                        a.State = (byte)AgentState.WorkerReturn;
                        a.Carry = 0f;
                        a.StateTimer = 0f;
                        break;
                    }
                    if (a.Health < t.Health * p.FleeHealth && alarm[tile] > p.FleeAlarm)
                    {
                        a.State = (byte)AgentState.WorkerFlee;
                        break;
                    }
                    desired = SteeringField.Sample(steering, size, a.ResourceKind, a.Position);
                    break;
                }
                case AgentState.WorkerHarvest:
                {
                    moves = false;
                    a.StateTimer += dt;
                    var node = nodes[a.Target];
                    float rate = nodeInfo[node.Def].HarvestRate;
                    float duration = math.max(p.HarvestSeconds, t.Carry / rate);
                    if (a.StateTimer >= duration) a.Flags |= (byte)AgentFlags.RequestHarvest;
                    break;
                }
                case AgentState.WorkerReturn:
                {
                    desired = SteeringField.Sample(steering, size, SteeringField.ProfileReturn, a.Position);
                    if (a.Carry > 0f) a.Flags |= (byte)AgentFlags.DepositTrail; // 空手回巢不留路径
                    if (homing[tile] <= p.UnloadDistance) a.Flags |= (byte)AgentFlags.RequestUnload;
                    break;
                }
                case AgentState.WorkerFlee:
                {
                    desired = SteeringField.Sample(steering, size, SteeringField.ProfileReturn, a.Position) * 2f;
                    if (homing[tile] <= p.UnloadDistance || alarm[tile] < p.FleeAlarm * 0.25f)
                        a.State = (byte)(a.Carry > 0f ? AgentState.WorkerReturn : AgentState.WorkerSearch);
                    break;
                }
            }

            if (!moves)
            {
                a.Velocity = 0;
                a.Rng = rng;
                next[i] = a;
                return;
            }

            // 游荡：方向随机游走，提供探索与打破对称
            a.WanderAngle += (AgentRandom.NextFloat(ref rng) - 0.5f) * 2f * p.WanderTurn * dt;
            float2 wander = new float2(math.cos(a.WanderAngle), math.sin(a.WanderAngle)) * p.Wander;
            float2 dir = desired + wander + Separation(i, a.Position) * p.SeparationStrength;
            if (math.lengthsq(dir) < 1e-6f) dir = wander;

            float speed = t.Speed * (temperature[tile] < p.ColdStop ? p.ColdMultiplier : 1f);
            float2 targetVel = math.normalizesafe(dir) * speed;
            a.Velocity = math.lerp(a.Velocity, targetVel, math.min(1f, p.Response * dt));

            float2 np = a.Position + a.Velocity * dt;
            if (CanMove(a.Position, np)) a.Position = np;
            else if (CanMove(a.Position, new float2(np.x, a.Position.y))) { a.Position.x = np.x; a.Velocity.y = 0; }
            else if (CanMove(a.Position, new float2(a.Position.x, np.y))) { a.Position.y = np.y; a.Velocity.x = 0; }
            else
            {
                a.Velocity *= -0.3f;
                a.WanderAngle += math.PI;
            }
            a.Position = math.clamp(a.Position, 0.01f, size - 0.01f);
            a.Rng = rng;
            next[i] = a;
        }

        int Tile(float2 q)
        {
            int x = math.clamp((int)math.floor(q.x), 0, size - 1), y = math.clamp((int)math.floor(q.y), 0, size - 1);
            return y * size + x;
        }

        /// <summary>按格子拓扑检查一步移动：跨格时必须经过开放的边（对角需两条路径之一可走）。</summary>
        bool CanMove(float2 from, float2 to)
        {
            if (to.x < 0 || to.y < 0 || to.x >= size || to.y >= size) return false;
            int a = Tile(from), b = Tile(to);
            if (a == b) return true;
            if ((open[a] & Pheromones.Walkable) == 0) return (open[b] & Pheromones.Walkable) != 0; // 卡在墙里：允许走出
            int ax = a % size, ay = a / size, bx = b % size, by = b / size;
            int dx = math.clamp(bx - ax, -1, 1), dy = math.clamp(by - ay, -1, 1);
            if (dx != 0 && dy != 0)
                return (Edge(a, dx, 0) && Edge(a + dx, 0, dy)) || (Edge(a, 0, dy) && Edge(a + dy * size, dx, 0));
            return Edge(a, dx, dy);
        }

        bool Edge(int i, int dx, int dy)
        {
            byte m = open[i];
            if (dx > 0) return (m & Pheromones.OpenPosX) != 0;
            if (dx < 0) return (m & Pheromones.OpenNegX) != 0;
            if (dy > 0) return (m & Pheromones.OpenPosY) != 0;
            return (m & Pheromones.OpenNegY) != 0;
        }

        /// <summary>分离力：只看所在格及 8 邻格，最多 16 个邻居（拥挤处不会退化成 O(n²)）。</summary>
        float2 Separation(int self, float2 pos)
        {
            float r = p.SeparationRadius, r2 = r * r;
            int cx = math.clamp((int)(pos.x / cellSize), 0, cellsPerSide - 1);
            int cy = math.clamp((int)(pos.y / cellSize), 0, cellsPerSide - 1);
            float2 push = 0;
            int checkedCount = 0;
            for (int oy = -1; oy <= 1; oy++)
            for (int ox = -1; ox <= 1; ox++)
            {
                int x = cx + ox, y = cy + oy;
                if ((uint)x >= (uint)cellsPerSide || (uint)y >= (uint)cellsPerSide) continue;
                int c = y * cellsPerSide + x;
                for (int k = cellStart[c]; k < cellStart[c + 1]; k++)
                {
                    int j = sorted[k];
                    if (j == self) continue;
                    float2 d = pos - cur[j].Position;
                    float l2 = math.lengthsq(d);
                    if (l2 < r2 && l2 > 1e-8f)
                    {
                        float l = math.sqrt(l2);
                        push += d / l * (1f - l / r);
                    }
                    if (++checkedCount >= 16) return push;
                }
            }
            return push;
        }
    }

    /// <summary>
    /// 单线程结算（按单位索引顺序，结果确定）：采集扣储量、卸货入库、留路径信息素、按缺口权重选资源。
    /// </summary>
    [BurstCompile(CompileSynchronously = true)]
    struct ResolveJob : IJob
    {
        public int size;
        public int trailOffset;
        public float trailDeposit;
        public NativeArray<AgentData> agents;
        public NativeArray<ResourceNode> nodes;
        [ReadOnly] public NativeArray<NodeInfo> nodeInfo;
        [ReadOnly] public NativeArray<TierParams> tiers;
        public NativeArray<float> source;
        public NativeArray<float> stock;
        [ReadOnly] public NativeArray<float> capacity;
        public NativeArray<float> delivered;
        [ReadOnly] public NativeArray<float> gatherWeights;
        public NativeArray<byte> dirtyChunks;

        public void Execute()
        {
            for (int i = 0; i < agents.Length; i++)
            {
                var a = agents[i];
                if (!a.IsAlive || (a.Flags & (byte)AgentFlags.RequestMask) == 0) continue;
                uint rng = a.Rng;

                if (a.Has(AgentFlags.DepositTrail))
                {
                    int x = math.clamp((int)a.Position.x, 0, size - 1), y = math.clamp((int)a.Position.y, 0, size - 1);
                    source[trailOffset + y * size + x] += trailDeposit;
                }

                if (a.Has(AgentFlags.RequestHarvest))
                {
                    var node = nodes[a.Target];
                    if (ResourceLookup.Harvestable(node))
                    {
                        var info = nodeInfo[node.Def];
                        float carry = tiers[a.Tier].Carry;
                        float grant = node.Infinite != 0 ? carry : math.min(carry, node.Amount);
                        if (node.Infinite == 0)
                        {
                            node.Amount -= grant;
                            if (node.Amount <= 1e-4f) { node.Amount = 0f; node.RegrowTimer = 0f; }
                            nodes[a.Target] = node;
                            dirtyChunks[node.Chunk] = 1;
                        }
                        a.Carry = grant;
                        a.CarryKind = info.Kind;
                        a.State = (byte)AgentState.WorkerReturn;
                    }
                    else a.State = (byte)AgentState.WorkerSearch;
                    a.Target = -1;
                    a.StateTimer = 0f;
                }

                if (a.Has(AgentFlags.RequestUnload))
                {
                    int k = a.CarryKind;
                    float add = math.min(a.Carry, math.max(0f, capacity[k] - stock[k]));
                    stock[k] += add;
                    delivered[k] += add;
                    a.Carry = 0f;
                    a.State = (byte)AgentState.WorkerSearch;
                    a.StateTimer = 0f;
                    a.ResourceKind = Choose(ref rng);
                }

                if (a.Has(AgentFlags.RequestRechoose)) a.ResourceKind = Choose(ref rng);

                a.Flags = (byte)(a.Flags & ~(byte)AgentFlags.RequestMask);
                a.Rng = rng;
                agents[i] = a;
            }
        }

        byte Choose(ref uint rng)
        {
            float w0 = gatherWeights[0], w1 = gatherWeights[1], w2 = gatherWeights[2];
            float r = AgentRandom.NextFloat(ref rng) * (w0 + w1 + w2);
            if (r < w0) return 0;
            if (r < w0 + w1) return 1;
            return 2;
        }
    }
}
