using NUnit.Framework;
using Swarm.Core;

namespace Swarm.Tests
{
    public class TickClockTests
    {
        static void Run(TickClock clock, double frameDt, int frames)
        {
            for (int f = 0; f < frames; f++)
            {
                int steps = clock.Advance(frameDt);
                for (int i = 0; i < steps; i++) clock.Step();
            }
        }

        [Test]
        public void OneSecond_Produces_10_5_1()
        {
            var c = new TickClock();
            Run(c, 0.02, 50);
            Assert.AreEqual(10, c.Count10);
            Assert.AreEqual(5, c.Count5);
            Assert.AreEqual(1, c.Count1);
            Assert.AreEqual(1.0, c.GameTime, 1e-9);
        }

        [Test]
        public void DoubleSpeed_DoublesCounts()
        {
            var c = new TickClock();
            Run(c, 0.02 * 2, 50); // 1 秒真实时间 × 2 倍速
            Assert.AreEqual(20, c.Count10);
            Assert.AreEqual(10, c.Count5);
            Assert.AreEqual(2, c.Count1);
        }

        [Test]
        public void TripleSpeed_TriplesCounts()
        {
            var c = new TickClock();
            Run(c, 0.02 * 3, 50);
            Assert.AreEqual(30, c.Count10);
            Assert.AreEqual(15, c.Count5);
            Assert.AreEqual(3, c.Count1);
        }

        [Test]
        public void Paused_DoesNotAdvance()
        {
            var c = new TickClock();
            Run(c, 0.05, 3);
            long before = c.Count10;
            Run(c, 0.0, 100);
            Assert.AreEqual(before, c.Count10);
        }

        [Test]
        public void LargeFrame_IsCapped()
        {
            var c = new TickClock { MaxStepsPerFrame = 8 };
            int steps = c.Advance(10.0);
            Assert.AreEqual(8, steps);
            for (int i = 0; i < steps; i++) c.Step();
            Assert.AreEqual(0, c.Advance(0.0), "被丢弃的时间不应在之后补跑");
        }

        [Test]
        public void Alpha_TracksPartialStep()
        {
            var c = new TickClock();
            Assert.AreEqual(0, c.Advance(0.05));
            Assert.AreEqual(0.5f, c.Alpha10, 1e-5f);
        }

        [Test]
        public void SameSeed_SameRandom()
        {
            var a = new SimulationWorld(42).CreateRandom(RandomStreams.WorldGen);
            var b = new SimulationWorld(42).CreateRandom(RandomStreams.WorldGen);
            var other = new SimulationWorld(42).CreateRandom(RandomStreams.Ecology);
            Assert.AreEqual(a.NextUInt(), b.NextUInt());
            Assert.AreNotEqual(a.NextUInt(), other.NextUInt());
        }
    }
}
