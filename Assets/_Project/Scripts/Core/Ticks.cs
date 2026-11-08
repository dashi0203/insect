using System;

namespace Swarm.Core
{
    /// <summary>本步触发了哪些节拍。5 Hz 和 1 Hz 都落在 10 Hz 的步上，所以一步可同时触发多个。</summary>
    [Flags]
    public enum TickFlags
    {
        None = 0,
        Hz10 = 1 << 0,
        Hz5 = 1 << 1,
        Hz1 = 1 << 2,
    }

    /// <summary>
    /// 纯 C# 的固定节拍时钟（GDD 14.2）。
    /// 以 10 Hz 为基础步：每 2 步触发一次 5 Hz，每 10 步触发一次 1 Hz，
    /// 因此三种节拍在时间上严格对齐、顺序确定。时间用整数微秒累加，避免浮点漂移。
    /// </summary>
    public sealed class TickClock
    {
        public const long MicrosPerSecond = 1_000_000;
        public const long BasePeriodMicros = MicrosPerSecond / 10; // 10 Hz
        public const int StepsPer5Hz = 2;
        public const int StepsPer1Hz = 10;

        public const float Dt10 = 0.1f;
        public const float Dt5 = 0.2f;
        public const float Dt1 = 1f;

        /// <summary>每帧最多补跑的基础步数；超出的时间直接丢弃，防止卡顿后的“死亡螺旋”。</summary>
        public int MaxStepsPerFrame { get; set; } = 8;

        long _accumMicros;
        long _gameMicros;

        public long Count10 { get; private set; }
        public long Count5 { get; private set; }
        public long Count1 { get; private set; }

        /// <summary>已模拟的游戏时间（秒），只随实际执行的步前进。</summary>
        public double GameTime => _gameMicros / (double)MicrosPerSecond;

        /// <summary>距下一个 10 Hz 步的进度 [0,1)，渲染用来在上一步与当前步之间插值。</summary>
        public float Alpha10 => _accumMicros / (float)BasePeriodMicros;

        /// <summary>累加一帧的游戏时间（已乘倍速；暂停时传 0），返回本帧要执行的基础步数。</summary>
        public int Advance(double gameDeltaSeconds)
        {
            if (gameDeltaSeconds > 0)
                _accumMicros += (long)Math.Round(gameDeltaSeconds * MicrosPerSecond);

            long steps = _accumMicros / BasePeriodMicros;
            if (steps > MaxStepsPerFrame)
            {
                steps = MaxStepsPerFrame;
                _accumMicros = steps * BasePeriodMicros + _accumMicros % BasePeriodMicros;
            }
            return (int)steps;
        }

        /// <summary>执行一个基础步，返回触发的节拍。只能在 Advance 返回的步数内调用。</summary>
        public TickFlags Step()
        {
            if (_accumMicros < BasePeriodMicros)
                throw new InvalidOperationException("TickClock.Step called without a pending step.");

            _accumMicros -= BasePeriodMicros;
            _gameMicros += BasePeriodMicros;

            Count10++;
            var flags = TickFlags.Hz10;
            if (Count10 % StepsPer5Hz == 0) { Count5++; flags |= TickFlags.Hz5; }
            if (Count10 % StepsPer1Hz == 0) { Count1++; flags |= TickFlags.Hz1; }
            return flags;
        }
    }
}
