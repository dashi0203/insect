using UnityEngine;
using UnityEngine.InputSystem;

namespace Swarm.Rendering
{
    /// <summary>
    /// 星际 2 式 RTS 镜头（GDD 3.1）：俯角 56°、偏航固定、FOV 40°。
    /// WASD / 方向键 / 屏幕边缘 / 中键拖动平移；滚轮缩放，镜头高度 18–90。
    /// 焦点 = 镜头视线与地面 y = 0 的交点，夹在地图范围内。暂停时仍可操作（使用 unscaled 时间）。
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public sealed class RtsCamera : MonoBehaviour
    {
        [Header("Framing (GDD 3.1)")]
        [SerializeField] float pitch = 56f;
        [SerializeField] float fieldOfView = 40f;
        [SerializeField] float minHeight = 18f;
        [SerializeField] float maxHeight = 90f;

        [Header("Map bounds (tiles, GDD 2.1)")]
        [SerializeField] Vector2 mapMin = Vector2.zero;
        [SerializeField] Vector2 mapMax = new Vector2(192f, 192f);

        [Header("Pan")]
        [Tooltip("每秒平移距离 = 当前高度 × 该系数")]
        [SerializeField] float panSpeedPerHeight = 1.2f;
        [SerializeField] bool edgePan = true;
        [SerializeField] int edgePixels = 10;

        [Header("Zoom")]
        [Tooltip("每格滚轮改变的高度比例")]
        [SerializeField] float zoomStepFraction = 0.15f;
        [SerializeField] float zoomSmoothing = 12f;

        [Header("Start")]
        [SerializeField] Vector2 startFocus = new Vector2(96f, 96f);
        [SerializeField] float startHeight = 50f;

        Camera _cam;
        Vector2 _focus;
        float _height;
        float _targetHeight;
        bool _dragging;
        Vector3 _dragAnchor; // 拖动开始时鼠标下方的地面点

        public Vector2 Focus => _focus;
        public float Height => _height;

        void Awake()
        {
            _cam = GetComponent<Camera>();
            _cam.fieldOfView = fieldOfView;
            _focus = ClampFocus(startFocus);
            _height = _targetHeight = Mathf.Clamp(startHeight, minHeight, maxHeight);
            Apply();
        }

        /// <summary>设置焦点可移动范围（Tile 坐标），世界生成后按地图尺寸调用。</summary>
        public void SetBounds(Vector2 min, Vector2 max)
        {
            mapMin = min;
            mapMax = max;
            _focus = ClampFocus(_focus);
            Apply();
        }

        /// <summary>镜头焦点跳到指定 Tile 坐标（供小地图点击使用）。</summary>
        public void JumpTo(Vector2 tile)
        {
            _focus = ClampFocus(tile);
            _dragging = false;
            Apply();
        }

        void LateUpdate()
        {
            float dt = Time.unscaledDeltaTime;
            var kb = Keyboard.current;
            var mouse = Mouse.current;

            // 缩放
            if (mouse != null)
            {
                float scroll = Swarm.Input.SwarmInput.ScrollSteps;
                bool modifier = Swarm.Input.SwarmInput.BrushModifierHeld; // Ctrl / Shift + 滚轮留给笔刷
                if (scroll != 0f && !modifier && MouseInsideScreen(mouse))
                    _targetHeight = Mathf.Clamp(_targetHeight * Mathf.Pow(1f - zoomStepFraction, scroll), minHeight, maxHeight);
            }
            _height = Mathf.Lerp(_height, _targetHeight, 1f - Mathf.Exp(-zoomSmoothing * dt));

            // 中键拖动：保持拖动起点始终在鼠标下方
            if (mouse != null)
            {
                if (mouse.middleButton.wasPressedThisFrame && MouseInsideScreen(mouse)
                    && TryGroundPoint(mouse.position.ReadValue(), out _dragAnchor))
                    _dragging = true;
                if (!mouse.middleButton.isPressed)
                    _dragging = false;
            }

            if (_dragging)
            {
                Apply();
                if (TryGroundPoint(mouse.position.ReadValue(), out var now))
                {
                    Vector3 delta = _dragAnchor - now;
                    _focus = ClampFocus(_focus + new Vector2(delta.x, delta.z));
                }
            }
            else
            {
                Vector2 dir = Vector2.zero;
                if (kb != null)
                {
                    if (kb.wKey.isPressed || kb.upArrowKey.isPressed) dir.y += 1f;
                    if (kb.sKey.isPressed || kb.downArrowKey.isPressed) dir.y -= 1f;
                    if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) dir.x += 1f;
                    if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) dir.x -= 1f;
                }
                if (edgePan && mouse != null && Application.isFocused && MouseInsideScreen(mouse))
                {
                    Vector2 p = mouse.position.ReadValue();
                    if (p.x <= edgePixels) dir.x -= 1f;
                    if (p.x >= Screen.width - 1 - edgePixels) dir.x += 1f;
                    if (p.y <= edgePixels) dir.y -= 1f;
                    if (p.y >= Screen.height - 1 - edgePixels) dir.y += 1f;
                }
                if (dir != Vector2.zero)
                {
                    dir = Vector2.ClampMagnitude(dir, 1f);
                    _focus = ClampFocus(_focus + dir * (panSpeedPerHeight * _height * dt));
                }
            }

            Apply();
        }

        void Apply()
        {
            float back = _height / Mathf.Tan(pitch * Mathf.Deg2Rad);
            transform.SetPositionAndRotation(
                new Vector3(_focus.x, _height, _focus.y - back),
                Quaternion.Euler(pitch, 0f, 0f));
        }

        bool TryGroundPoint(Vector2 screenPos, out Vector3 point)
        {
            Ray ray = _cam.ScreenPointToRay(screenPos);
            if (ray.direction.y < -1e-4f)
            {
                float t = -ray.origin.y / ray.direction.y;
                point = ray.origin + ray.direction * t;
                return true;
            }
            point = default;
            return false;
        }

        Vector2 ClampFocus(Vector2 f) =>
            new Vector2(Mathf.Clamp(f.x, mapMin.x, mapMax.x), Mathf.Clamp(f.y, mapMin.y, mapMax.y));

        static bool MouseInsideScreen(Mouse mouse)
        {
            Vector2 p = mouse.position.ReadValue();
            return p.x >= 0 && p.y >= 0 && p.x < Screen.width && p.y < Screen.height;
        }
    }
}
