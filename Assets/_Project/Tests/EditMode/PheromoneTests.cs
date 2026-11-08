using System.Diagnostics;
using NUnit.Framework;
using Swarm.Data;
using Swarm.Fields;
using Swarm.World;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Swarm.Tests
{
    public class PheromoneTests
    {
        const int N = 32;

        static GameBalance Balance()
        {
            var b = ScriptableObject.CreateInstance<GameBalance>();
            b.pheromoneChannels = GameBalance.DefaultPheromoneChannels();
            return b;
        }

        static NativeArray<byte> Coverage(int size, System.Func<int, int, bool> covered)
        {
            var c = new NativeArray<byte>(size * size, Allocator.Persistent);
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                c[y * size + x] = (byte)(covered(x, y) ? 1 : 0);
            return c;
        }

        [Test]
        public void Diffusion_ConservesMass_WithoutDecay()
        {
            var b = Balance();
            b.pheromoneChannels[(int)PheromoneChannel.Trail].decayInside = 0f;
            b.pheromoneChannels[(int)PheromoneChannel.Trail].decayOutside = 0f;
            using var env = new EnvGrid(N);
            using var f = new PheromoneFields(env, b);
            using var cov = Coverage(N, (x, y) => true);
            f.Deposit(PheromoneChannel.Trail, env.Index(16, 16), 5f);
            for (int s = 0; s < 50; s++) f.Step(0.2f, cov);
            Assert.AreEqual(5f, f.Total(PheromoneChannel.Trail), 1e-3f);
            Assert.Greater(f.Get(PheromoneChannel.Trail, env.Index(18, 16)), 0f, "should have spread");
        }

        [Test]
        public void Walls_BlockDiffusion()
        {
            var b = Balance();
            using var env = new EnvGrid(N);
            for (int y = 0; y < N; y++) env.Flags[env.Index(16, y)] = (byte)TileFlags.Obstacle; // 一整列高山
            using var f = new PheromoneFields(env, b);
            using var cov = Coverage(N, (x, y) => true);
            f.Deposit(PheromoneChannel.Alarm, env.Index(14, 10), 10f);
            for (int s = 0; s < 100; s++) f.Step(0.2f, cov);
            for (int y = 0; y < N; y++)
            for (int x = 17; x < N; x++)
                Assert.AreEqual(0f, f.Get(PheromoneChannel.Alarm, env.Index(x, y)));
        }

        [Test]
        public void CliffWithoutRamp_Blocks_RampLetsThrough()
        {
            var b = Balance();
            using var env = new EnvGrid(N);
            for (int y = 0; y < N; y++)
            for (int x = 16; x < N; x++)
                env.HeightLevel[env.Index(x, y)] = 1;
            using var f = new PheromoneFields(env, b);
            using var cov = Coverage(N, (x, y) => true);

            f.Deposit(PheromoneChannel.Trail, env.Index(12, 16), 8f);
            for (int s = 0; s < 200; s++) f.Step(0.2f, cov);
            Assert.AreEqual(0f, f.Get(PheromoneChannel.Trail, env.Index(18, 16)), "cliff edge without ramp is a wall");

            for (int y = 14; y <= 17; y++) env.Flags[env.Index(16, y)] |= (byte)TileFlags.Ramp;
            f.BuildOpenMask(env);
            for (int s = 0; s < 200; s++) f.Step(0.2f, cov);
            Assert.Greater(f.Get(PheromoneChannel.Trail, env.Index(18, 16)), 0f, "ramp opens the edge");
        }

        [Test]
        public void Brush_OnlyWorksInsideCoverage()
        {
            var b = Balance();
            using var env = new EnvGrid(N);
            using var f = new PheromoneFields(env, b);
            using var cov = Coverage(N, (x, y) => x < 16); // 左半边有覆盖
            int painted = f.Paint(PheromoneChannel.Gather, new float2(16f, 16f), 8f, 1f, cov);
            Assert.Greater(painted, 0);
            for (int y = 0; y < N; y++)
            for (int x = 16; x < N; x++)
                Assert.AreEqual(0f, f.GetPaint(PheromoneChannel.Gather, env.Index(x, y)), $"painted outside coverage at {x},{y}");
            Assert.Greater(f.GetPaint(PheromoneChannel.Gather, env.Index(14, 16)), 0f);

            int none = f.Paint(PheromoneChannel.Rally, new float2(26f, 16f), 3f, 1f, cov);
            Assert.AreEqual(0, none, "brush entirely outside coverage does nothing");
        }

        [Test]
        public void Brush_TakesMax_DoesNotAccumulate()
        {
            var b = Balance();
            using var env = new EnvGrid(N);
            using var f = new PheromoneFields(env, b);
            using var cov = Coverage(N, (x, y) => true);
            for (int k = 0; k < 5; k++) f.Paint(PheromoneChannel.Gather, new float2(16.5f, 16.5f), 4f, 0.5f, cov);
            Assert.AreEqual(0.5f, f.GetPaint(PheromoneChannel.Gather, env.Index(16, 16)), 1e-5f);
        }

        [Test]
        public void PaintedField_PersistsInsideCoverage_FadesOutside()
        {
            var b = Balance();
            using var env = new EnvGrid(N);
            using var f = new PheromoneFields(env, b);
            using var all = Coverage(N, (x, y) => true);
            using var none = Coverage(N, (x, y) => false);
            f.Paint(PheromoneChannel.Gather, new float2(16.5f, 16.5f), 3f, 1f, all);
            for (int s = 0; s < 100; s++) f.Step(0.2f, all);
            float kept = f.Get(PheromoneChannel.Gather, env.Index(16, 16));
            Assert.Greater(kept, 0.8f, "inside coverage the painted field is permanent");
            for (int s = 0; s < 300; s++) f.Step(0.2f, none); // 失去覆盖 60 秒
            Assert.Less(f.Get(PheromoneChannel.Gather, env.Index(16, 16)), kept * 0.1f, "outside coverage it fades away");
        }

        [Test]
        public void Homing_IsBfsDistanceFromEntrance()
        {
            var b = Balance();
            using var env = new EnvGrid(N);
            using var f = new PheromoneFields(env, b);
            f.RecomputeHoming(new[] { new int2(5, 5) });
            Assert.AreEqual(0, f.HomingDistance[env.Index(5, 5)]);
            Assert.AreEqual(7, f.HomingDistance[env.Index(9, 8)]);
            var dir = f.HomingDirection(new float2(9.5f, 5.5f));
            Assert.Less(dir.x, 0f, "points back toward the entrance");
        }

        [Test]
        public void SurfaceStep_PerformanceAt384()
        {
            var b = Balance();
            using var env = new EnvGrid(384);
            using var f = new PheromoneFields(env, b);
            using var cov = new NativeArray<byte>(env.Length, Allocator.Persistent);
            for (int i = 0; i < env.Length; i += 37) f.Deposit(PheromoneChannel.Trail, i, 3f);
            for (int s = 0; s < 3; s++) f.Step(0.2f, cov); // 预热（Burst 同步编译）
            var sw = Stopwatch.StartNew();
            const int runs = 20;
            for (int s = 0; s < runs; s++) f.Step(0.2f, cov);
            double ms = sw.Elapsed.TotalMilliseconds / runs;
            UnityEngine.Debug.Log($"[Swarm] Pheromone step, 9 channels x 384^2 tiles: {ms:0.00} ms (target < 2 ms in a player build)");
            // 编辑器里开着安全检查会更慢，这里只防止数量级退化；真正的 < 2 ms 以运行时调试面板为准
            Assert.Less(ms, 15.0);
        }
    }
}
