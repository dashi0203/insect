using System;
using UnityEngine;

namespace Swarm.Data
{
    public enum SizeTier : byte { Small = 0, Medium = 1, Large = 2, Huge = 3 }

    public enum AgentRole : byte { Worker = 0, Soldier = 1 }

    /// <summary>体型档位（GDD 5.1 / 5.2）。</summary>
    [Serializable]
    public struct SizeTierSettings
    {
        public SizeTier tier;
        public AgentRole role;
        public float proteinThreshold;
        public float mineralThreshold;
        public float health;
        [Tooltip("Tile / 秒")] public float speed;
        [Tooltip("蛋白质 / 分钟")] public float upkeepPerMinute;
        public float lifespanMinutes;
        [Tooltip("工虫携带量；兵虫为 0")] public float carry;
        public float attack;
        [Tooltip("秒")] public float attackInterval;
        public float armor;
        [Tooltip("Tile")] public float alertRadius;
        [Tooltip("外观缩放，1 = 1 Tile")] public float scale;

        public static SizeTierSettings[] Defaults() => new[]
        {
            new SizeTierSettings { tier = SizeTier.Small, role = AgentRole.Worker, proteinThreshold = 10, mineralThreshold = 2, health = 10, speed = 3.0f, upkeepPerMinute = 0.5f, lifespanMinutes = 8, carry = 1f, attack = 1, attackInterval = 1.2f, armor = 0, alertRadius = 3, scale = 0.6f },
            new SizeTierSettings { tier = SizeTier.Medium, role = AgentRole.Worker, proteinThreshold = 30, mineralThreshold = 8, health = 20, speed = 2.8f, upkeepPerMinute = 1.5f, lifespanMinutes = 10, carry = 1.5f, attack = 2, attackInterval = 1.2f, armor = 0, alertRadius = 3, scale = 0.85f },
            new SizeTierSettings { tier = SizeTier.Large, role = AgentRole.Soldier, proteinThreshold = 100, mineralThreshold = 40, health = 60, speed = 2.5f, upkeepPerMinute = 6f, lifespanMinutes = 12, carry = 0f, attack = 8, attackInterval = 1.0f, armor = 2, alertRadius = 6, scale = 1.2f },
            new SizeTierSettings { tier = SizeTier.Huge, role = AgentRole.Soldier, proteinThreshold = 350, mineralThreshold = 180, health = 160, speed = 2.0f, upkeepPerMinute = 25f, lifespanMinutes = 14, carry = 0f, attack = 18, attackInterval = 1.2f, armor = 4, alertRadius = 6, scale = 1.7f },
        };
    }

    /// <summary>某个行为状态对各通道梯度的响应权重（GDD 4.5）。正 = 吸引，负 = 排斥。</summary>
    [Serializable]
    public struct SteeringWeights
    {
        public float gather, rally, avoid, trail, alarm;
        [Tooltip("所选资源的气味")] public float scent;
        public float danger;
        public float homing;

        public SteeringWeights(float gather, float rally, float avoid, float trail, float alarm, float scent, float danger, float homing)
        {
            this.gather = gather; this.rally = rally; this.avoid = avoid; this.trail = trail;
            this.alarm = alarm; this.scent = scent; this.danger = danger; this.homing = homing;
        }

        public static SteeringWeights WorkerSearch => new SteeringWeights(1.0f, 0f, -1.5f, 0.8f, -0.5f, 0.6f, -1.0f, 0f);
        public static SteeringWeights WorkerReturn => new SteeringWeights(0f, 0f, -1.5f, 0.3f, -0.3f, 0f, -0.8f, 1.0f);
        public static SteeringWeights SoldierIdle => new SteeringWeights(0f, 1.0f, -1.0f, 0f, 1.2f, 0f, -0.6f, 0.2f);
    }
}
