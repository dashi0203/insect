using UnityEngine;
using UnityEngine.InputSystem;

namespace Swarm.Input
{
    /// <summary>可绑定的玩家与调试动作。新增按键在这里加一项，并在 SwarmInput.Bindings 里给出默认键。</summary>
    public enum SwarmAction
    {
        TogglePause,
        Speed1, Speed2, Speed3,
        BrushGather, BrushRally, BrushAvoid, BrushErase, BrushCancel,
        DebugPlaceNode,
        DebugNextRegion, DebugNewSeed, DebugRegenerate, DebugTerrainOverlay, DebugDepleteResources, DebugNextSeason,
        DebugSpawnWorkers,
    }

    /// <summary>
    /// 全部键盘 / 鼠标输入的唯一入口：按键绑定集中在这里，其他代码只问“某动作是否触发”。
    /// 镜头平移（WASD / 边缘 / 中键）和滚轮缩放仍由 RtsCamera 直接读取，但它通过 BrushModifierHeld 避让笔刷的 Ctrl / Shift + 滚轮。
    /// </summary>
    public static class SwarmInput
    {
        // 4X / RTS 惯例：F1 / F2 / F3 = 1× / 2× / 3×；数字键 1–3 留给信息素笔刷（GDD 4.3）
        static readonly Key[][] Bindings =
        {
            new[] { Key.Space, Key.Pause },  // TogglePause
            new[] { Key.F1 },                // Speed1
            new[] { Key.F2 },                // Speed2
            new[] { Key.F3 },                // Speed3
            new[] { Key.Digit1 },            // BrushGather
            new[] { Key.Digit2 },            // BrushRally
            new[] { Key.Digit3 },            // BrushAvoid
            new[] { Key.E },                 // BrushErase
            new[] { Key.Escape },            // BrushCancel
            new[] { Key.B },                 // DebugPlaceNode
            new[] { Key.Tab },               // DebugNextRegion
            new[] { Key.N },                 // DebugNewSeed
            new[] { Key.R },                 // DebugRegenerate
            new[] { Key.M },                 // DebugTerrainOverlay
            new[] { Key.H },                 // DebugDepleteResources
            new[] { Key.RightBracket },      // DebugNextSeason
            new[] { Key.K },                 // DebugSpawnWorkers（Shift = ×10）
        };

        /// <summary>UI 在指针下方时由 UI 层置位，世界交互（笔刷、悬停）据此忽略指针。</summary>
        public static bool PointerOverUi { get; set; }

        public static bool Pressed(SwarmAction action)
        {
            var kb = Keyboard.current;
            if (kb == null) return false;
            foreach (var key in Bindings[(int)action])
                if (kb[key].wasPressedThisFrame) return true;
            return false;
        }

        public static bool HasPointer => Mouse.current != null;
        public static Vector2 PointerPosition => Mouse.current != null ? Mouse.current.position.ReadValue() : Vector2.zero;
        public static bool PrimaryHeld => Mouse.current != null && Mouse.current.leftButton.isPressed;

        public static bool CtrlHeld => Keyboard.current != null && Keyboard.current.ctrlKey.isPressed;
        public static bool ShiftHeld => Keyboard.current != null && Keyboard.current.shiftKey.isPressed;
        /// <summary>Ctrl / Shift + 滚轮调笔刷半径 / 强度，此时镜头不缩放。</summary>
        public static bool BrushModifierHeld => CtrlHeld || ShiftHeld;

        /// <summary>本帧滚轮格数（已把每格 120 的旧平台换算成 1）。</summary>
        public static float ScrollSteps
        {
            get
            {
                if (Mouse.current == null) return 0f;
                float s = Mouse.current.scroll.ReadValue().y;
                return Mathf.Abs(s) > 10f ? s / 120f : s;
            }
        }
    }
}
