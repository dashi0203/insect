using System;
using Swarm.Input;
using UnityEngine;

namespace Swarm.Core
{
    /// <summary>
    /// 驱动固定节拍的唯一入口（GDD 14.2）：把帧时间换算成 10 Hz 基础步，每步调用一次 OnStep。
    /// 5 Hz / 1 Hz 落在同一基础步上，由 TickFlags 标明；订阅者（GameRoot → SimulationWorld.Step）按标志分派。
    /// 只负责时间：暂停、倍速；不持有任何模拟状态。
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    public sealed class GameLoop : MonoBehaviour
    {
        public static GameLoop Instance { get; private set; }
        public const int MaxSpeed = 3;

        [SerializeField] bool startPaused = false;

        /// <summary>每个基础步一次，参数为本步触发的节拍。</summary>
        public event Action<TickFlags> OnStep;

        readonly TickClock _clock = new TickClock();

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
            Paused = startPaused;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        public void SetPaused(bool paused) => Paused = paused;
        public void TogglePause() => Paused = !Paused;
        public void SetSpeed(int speed) => Speed = Mathf.Clamp(speed, 1, MaxSpeed);

        void Update()
        {
            if (SwarmInput.Pressed(SwarmAction.TogglePause)) TogglePause();
            if (SwarmInput.Pressed(SwarmAction.Speed1)) SetSpeed(1);
            if (SwarmInput.Pressed(SwarmAction.Speed2)) SetSpeed(2);
            if (SwarmInput.Pressed(SwarmAction.Speed3)) SetSpeed(3);

            double dt = Paused ? 0.0 : Time.unscaledDeltaTime * Speed;
            int steps = _clock.Advance(dt);
            for (int i = 0; i < steps; i++)
            {
                var flags = _clock.Step();
                OnStep?.Invoke(flags);
            }
        }
    }
}
