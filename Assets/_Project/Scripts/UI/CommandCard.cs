using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace Swarm.UI
{
    public enum BrushTool { None, Gather, Rally, Avoid, Erase }

    /// <summary>
    /// 命令卡（GDD 13.1 中下）：信息素笔刷与半径、强度；叠加层开关；调试用的“放置信息素节点”。
    /// 代码构建的 UI Toolkit 元素，挂在场景里已有的 UIDocument 上。
    /// </summary>
    public sealed class CommandCard
    {
        public static readonly string[] OverlayNames =
        {
            "Gather", "Rally", "Avoid", "Trail", "Alarm",
            "Protein", "Mineral", "Build", "Danger", "Homing", "Coverage",
        };

        public event Action<BrushTool> ToolSelected;
        public event Action<float> RadiusChanged;
        public event Action<float> StrengthChanged;
        public event Action<int, bool> OverlayToggled;
        public event Action PlaceNodeClicked;

        public bool PointerOver { get; private set; }

        readonly VisualElement _root;
        readonly Button[] _toolButtons;
        readonly Slider _radius, _strength;
        readonly Toggle[] _overlays;
        readonly Label _info;

        static readonly Color Panel = new Color(0.05f, 0.06f, 0.05f, 0.82f);
        static readonly Color Active = new Color(0.32f, 0.5f, 0.25f, 1f);
        static readonly Color Idle = new Color(0.18f, 0.2f, 0.18f, 1f);

        public CommandCard(VisualElement parent, float radiusMin, float radiusMax, float strengthMin, float strengthMax,
            Color[] overlayColors)
        {
            _root = new VisualElement { name = "CommandCard" };
            var s = _root.style;
            s.position = Position.Absolute;
            s.bottom = 14;
            s.left = new Length(50, LengthUnit.Percent);
            s.translate = new Translate(new Length(-50, LengthUnit.Percent), 0);
            s.paddingLeft = s.paddingRight = 10;
            s.paddingTop = s.paddingBottom = 8;
            s.backgroundColor = Panel;
            s.borderTopLeftRadius = s.borderTopRightRadius = s.borderBottomLeftRadius = s.borderBottomRightRadius = 6;
            s.minWidth = 620;
            _root.RegisterCallback<PointerEnterEvent>(_ => PointerOver = true);
            _root.RegisterCallback<PointerLeaveEvent>(_ => PointerOver = false);

            // 笔刷
            var tools = Row();
            var defs = new (BrushTool tool, string label)[]
            {
                (BrushTool.Gather, "Gather [1]"), (BrushTool.Rally, "Rally [2]"), (BrushTool.Avoid, "Avoid [3]"),
                (BrushTool.Erase, "Erase [E]"), (BrushTool.None, "Off [Esc]"),
            };
            _toolButtons = new Button[defs.Length];
            for (int k = 0; k < defs.Length; k++)
            {
                var t = defs[k].tool;
                var btn = new Button(() => ToolSelected?.Invoke(t)) { text = defs[k].label, userData = t };
                btn.style.backgroundColor = Idle;
                btn.style.color = Color.white;
                btn.style.minWidth = 90;
                _toolButtons[k] = btn;
                tools.Add(btn);
            }
            var nodeBtn = new Button(() => PlaceNodeClicked?.Invoke()) { text = "Node [B]" };
            nodeBtn.style.backgroundColor = new Color(0.22f, 0.25f, 0.32f);
            nodeBtn.style.color = Color.white;
            nodeBtn.style.marginLeft = 12;
            tools.Add(nodeBtn);
            _root.Add(tools);

            // 半径 / 强度
            var sliders = Row();
            _radius = new Slider("Radius (Ctrl+wheel)", radiusMin, radiusMax) { showInputField = false };
            _radius.style.flexGrow = 1;
            _radius.RegisterValueChangedCallback(e => RadiusChanged?.Invoke(e.newValue));
            _strength = new Slider("Strength (Shift+wheel)", strengthMin, strengthMax) { showInputField = false };
            _strength.style.flexGrow = 1;
            _strength.style.marginLeft = 12;
            _strength.RegisterValueChangedCallback(e => StrengthChanged?.Invoke(e.newValue));
            StyleLabel(_radius.labelElement);
            StyleLabel(_strength.labelElement);
            sliders.Add(_radius);
            sliders.Add(_strength);
            _root.Add(sliders);

            // 叠加层
            var overlays = Row();
            overlays.style.flexWrap = Wrap.Wrap;
            _overlays = new Toggle[OverlayNames.Length];
            for (int k = 0; k < OverlayNames.Length; k++)
            {
                int index = k;
                var tg = new Toggle(OverlayNames[k]);
                tg.style.marginRight = 8;
                StyleLabel(tg.labelElement);
                if (overlayColors != null && k < overlayColors.Length) tg.labelElement.style.color = overlayColors[k];
                tg.RegisterValueChangedCallback(e => OverlayToggled?.Invoke(index, e.newValue));
                _overlays[k] = tg;
                overlays.Add(tg);
            }
            _root.Add(overlays);

            _info = new Label();
            StyleLabel(_info);
            _info.style.fontSize = 11;
            _info.style.color = new Color(0.75f, 0.85f, 0.75f);
            _root.Add(_info);

            parent.Add(_root);
        }

        static VisualElement Row()
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.marginBottom = 4;
            return row;
        }

        static void StyleLabel(VisualElement label)
        {
            if (label == null) return;
            label.style.color = new Color(0.88f, 0.92f, 0.88f);
            label.style.minWidth = StyleKeyword.Auto;
        }

        public void SetTool(BrushTool tool)
        {
            foreach (var b in _toolButtons)
                b.style.backgroundColor = (BrushTool)b.userData == tool ? Active : Idle;
        }

        public void SetRadius(float v) => _radius.SetValueWithoutNotify(v);
        public void SetStrength(float v) => _strength.SetValueWithoutNotify(v);
        public void SetOverlay(int index, bool on) => _overlays[index].SetValueWithoutNotify(on);
        public void SetInfo(string text) => _info.text = text;

        public void Remove() => _root.RemoveFromHierarchy();
    }
}
