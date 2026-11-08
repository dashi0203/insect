using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Swarm.Core
{
    /// <summary>
    /// 驱动三种固定节拍（10 / 5 / 1 Hz）的唯一入口（GDD 14.2）。
    /// 其他系统订阅 OnTick10 / OnTick5 / OnTick1，参数为该节拍的固定 dt（秒）。
    /// 同一步内的顺序固定为 10 Hz → 5 Hz → 1 Hz。
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    public sealed class GameLoop : MonoBehaviour
    {
        public static GameLoop Instance { get; private set; }

        [SerializeField] uint worldSeed = 12345;
        [SerializeField] bool startPaused = false;

        public event Action<float> OnTick10;
        public event Action<float> OnTick5;
        public event Action<float> OnTick1;

        readonly TickClock _clock = new TickClock();

        public SimulationWorld World { get; private set; }
        public TickClock Clock => _clock;
        public bool Paused { get; private set; }
        public int Speed { get; private set; } = 1;

        /// <summary>渲染插值系数：0 = 上一个 10 Hz 步的状态，1 = 当前步的状态。</summary>
        public float RenderAlpha => _clock.Alpha10;

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning("Duplicate GameLoop destroyed.", this);
                Destroy(gameObject);
                return;
            }
            Instance = this;
            World = new SimulationWorld(worldSeed);
            Paused = startPaused;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        public void SetPaused(bool paused) => Paused = paused;
        public void TogglePause() => Paused = !Paused;
        public const int MaxSpeed = 3;
        public void SetSpeed(int speed) => Speed = Mathf.Clamp(speed, 1, MaxSpeed);

        void Update()
        {
            HandleInput();

            double dt = Paused ? 0.0 : Time.unscaledDeltaTime * Speed;
            int steps = _clock.Advance(dt);
            for (int i = 0; i < steps; i++)
            {
                var flags = _clock.Step();
                OnTick10?.Invoke(TickClock.Dt10);
                if ((flags & TickFlags.Hz5) != 0) OnTick5?.Invoke(TickClock.Dt5);
                if ((flags & TickFlags.Hz1) != 0) OnTick1?.Invoke(TickClock.Dt1);
            }
        }

        void HandleInput()
        {
            var kb = Keyboard.current;
            if (kb == null) return;
            if (kb.spaceKey.wasPressedThisFrame || kb.pauseKey.wasPressedThisFrame) TogglePause();
            // 4X / RTS 惯例：F1 / F2 / F3 = 1× / 2× / 3×（数字键 1–3 留给信息素笔刷，GDD 4.3）
            if (kb.f1Key.wasPressedThisFrame) SetSpeed(1);
            if (kb.f2Key.wasPressedThisFrame) SetSpeed(2);
            if (kb.f3Key.wasPressedThisFrame) SetSpeed(3);
        }
    }
}
