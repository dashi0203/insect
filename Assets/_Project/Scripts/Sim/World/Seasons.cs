using Swarm.Data;
using Unity.Mathematics;

namespace Swarm.World
{
    /// <summary>
    /// 季节时钟（GDD 2.5）：一年 yearLength 秒，冬季占区域给定比例，春夏秋均分其余时间。
    /// 温度季节曲线：盛夏（夏季中点）+A，隆冬（冬季中点）−A，两段之间用余弦平滑过渡（两段长度可以不同）。
    /// 每局从春季开始。
    /// </summary>
    public sealed class SeasonClock
    {
        public readonly float YearLength;
        public readonly float WinterFraction;
        public readonly float Amplitude;

        public double Time { get; private set; }

        public SeasonClock(float yearLength, float winterFraction, float amplitude)
        {
            YearLength = math.max(1f, yearLength);
            WinterFraction = math.clamp(winterFraction, 0f, 0.97f);
            Amplitude = amplitude;
        }

        public float TimeOfYear => (float)(Time % YearLength);
        public int Year => (int)(Time / YearLength);
        public Season Current => SeasonAt(TimeOfYear, YearLength, WinterFraction);
        public float TemperatureOffset => OffsetAt(TimeOfYear, YearLength, WinterFraction, Amplitude);

        /// <summary>当前季节已过去的比例 0..1。</summary>
        public float SeasonProgress
        {
            get
            {
                float t = TimeOfYear;
                float start = SeasonStart(Current, YearLength, WinterFraction);
                return math.saturate((t - start) / SeasonLength(Current, YearLength, WinterFraction));
            }
        }

        /// <summary>推进时间，返回季节是否发生变化。</summary>
        public bool Advance(float dt)
        {
            var before = Current;
            Time += dt;
            return Current != before;
        }

        /// <summary>跳到下一季节开始（调试用）。</summary>
        public void SkipToNextSeason()
        {
            float t = TimeOfYear;
            var next = (Season)(((int)Current + 1) % 4);
            float start = SeasonStart(next, YearLength, WinterFraction);
            float delta = start - t;
            if (delta <= 0f) delta += YearLength;
            Time += delta + 1e-3;
        }

        public static float SeasonLength(Season s, float year, float winter) =>
            s == Season.Winter ? year * winter : year * (1f - winter) / 3f;

        public static float SeasonStart(Season s, float year, float winter) =>
            year * (1f - winter) / 3f * (int)s;

        public static Season SeasonAt(float t, float year, float winter)
        {
            float a = year * (1f - winter) / 3f;
            t = Mod(t, year);
            if (t < a) return Season.Spring;
            if (t < 2f * a) return Season.Summer;
            if (t < 3f * a) return Season.Autumn;
            return Season.Winter;
        }

        public static float OffsetAt(float t, float year, float winter, float amplitude)
        {
            float a = year * (1f - winter) / 3f;
            float midSummer = 1.5f * a;
            float midWinter = 3f * a + year * winter * 0.5f;
            float cooling = math.max(1e-3f, midWinter - midSummer);
            float warming = math.max(1e-3f, year - cooling);
            float u = Mod(t - midSummer, year);
            return u < cooling
                ? amplitude * math.cos(math.PI * u / cooling)
                : -amplitude * math.cos(math.PI * (u - cooling) / warming);
        }

        static float Mod(float x, float m)
        {
            float r = x % m;
            return r < 0f ? r + m : r;
        }
    }
}
