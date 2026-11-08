using Swarm.Core;
using Swarm.Rendering;
using UnityEngine;
using UnityEngine.UIElements;

namespace Swarm.UI
{
    /// <summary>
    /// M0 调试面板：三种节拍计数、暂停 / 倍速、游戏时间、FPS、镜头高度。
    /// 用 UI Toolkit；PanelSettings 由场景生成器创建并赋给 UIDocument。
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class DebugPanel : MonoBehaviour
    {
        [SerializeField] float refreshInterval = 0.2f;

        Label _label;
        RtsCamera _camera;
        float _nextRefresh;
        int _frames;
        float _fpsTimer;
        float _fps;

        void OnEnable()
        {
            var root = GetComponent<UIDocument>().rootVisualElement;
            var box = new VisualElement();
            box.style.position = Position.Absolute;
            box.style.left = 8;
            box.style.top = 8;
            box.style.paddingLeft = box.style.paddingRight = 10;
            box.style.paddingTop = box.style.paddingBottom = 6;
            box.style.backgroundColor = new Color(0f, 0f, 0f, 0.65f);
            box.style.borderTopLeftRadius = box.style.borderTopRightRadius =
                box.style.borderBottomLeftRadius = box.style.borderBottomRightRadius = 4;
            box.pickingMode = PickingMode.Ignore;

            _label = new Label();
            _label.style.color = new Color(0.85f, 1f, 0.85f);
            _label.style.fontSize = 14;
            _label.pickingMode = PickingMode.Ignore;
            box.Add(_label);
            root.Add(box);
        }

        void Update()
        {
            _frames++;
            _fpsTimer += Time.unscaledDeltaTime;
            if (_fpsTimer >= 0.5f)
            {
                _fps = _frames / _fpsTimer;
                _frames = 0;
                _fpsTimer = 0f;
            }

            if (Time.unscaledTime < _nextRefresh || _label == null) return;
            _nextRefresh = Time.unscaledTime + refreshInterval;

            var loop = GameLoop.Instance;
            if (_camera == null) _camera = FindFirstObjectByType<RtsCamera>();

            if (loop == null)
            {
                _label.text = "GameLoop missing";
                return;
            }

            var c = loop.Clock;
            string state = loop.Paused ? "PAUSED" : $"{loop.Speed}x";
            string cam = _camera != null
                ? $"Camera  h {_camera.Height:0.0}  focus ({_camera.Focus.x:0}, {_camera.Focus.y:0})"
                : "Camera  -";
            _label.text =
                $"Ticks  10Hz {c.Count10}   5Hz {c.Count5}   1Hz {c.Count1}\n" +
                $"Game time {c.GameTime:0.0}s   [{state}]   FPS {_fps:0}\n" +
                cam + "\n" +
                "Space pause   F1/F2/F3 speed   WASD/edge/MMB pan   wheel zoom" +
                (Swarm.World.WorldController.Instance != null ? "\n\n" + Swarm.World.WorldController.Instance.DebugText() : "") +
                (Swarm.Fields.PheromoneController.Instance != null ? "\n" + Swarm.Fields.PheromoneController.Instance.DebugText() : "");
        }
    }
}
