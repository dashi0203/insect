using UnityEngine;
using UnityEngine.UIElements;

namespace Swarm.UI
{
    /// <summary>
    /// 选中对象信息（GDD 13.1 右下）：选中单位后显示标题与多行说明，未选中时隐藏。
    /// 字体用系统中文字体（微软雅黑等），找不到时退回默认字体。
    /// </summary>
    public sealed class SelectionPanel
    {
        readonly VisualElement _root;
        readonly Label _title, _body;

        public bool PointerOver { get; private set; }

        public SelectionPanel(VisualElement parent)
        {
            _root = new VisualElement { name = "SelectionPanel" };
            var s = _root.style;
            s.position = Position.Absolute;
            s.right = 12;
            s.bottom = 12;
            s.width = 360;
            s.paddingLeft = s.paddingRight = 12;
            s.paddingTop = s.paddingBottom = 8;
            s.backgroundColor = new Color(0.05f, 0.06f, 0.05f, 0.86f);
            s.borderTopLeftRadius = s.borderTopRightRadius = s.borderBottomLeftRadius = s.borderBottomRightRadius = 6;
            s.display = DisplayStyle.None;

            var font = Font.CreateDynamicFontFromOSFont(
                new[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "PingFang SC", "Noto Sans CJK SC" }, 14);
            if (font != null) s.unityFontDefinition = FontDefinition.FromFont(font);

            _title = new Label();
            _title.style.color = new Color(0.95f, 0.9f, 0.6f);
            _title.style.fontSize = 15;
            _title.style.unityFontStyleAndWeight = FontStyle.Bold;
            _title.style.marginBottom = 4;
            _body = new Label();
            _body.style.color = new Color(0.86f, 0.92f, 0.86f);
            _body.style.fontSize = 13;
            _body.style.whiteSpace = WhiteSpace.Normal;
            _root.Add(_title);
            _root.Add(_body);

            _root.RegisterCallback<PointerEnterEvent>(_ => PointerOver = true);
            _root.RegisterCallback<PointerLeaveEvent>(_ => PointerOver = false);
            parent.Add(_root);
        }

        public void Show(string title, string body)
        {
            _root.style.display = DisplayStyle.Flex;
            _title.text = title;
            _body.text = body;
        }

        public void Hide()
        {
            _root.style.display = DisplayStyle.None;
            PointerOver = false;
        }

        public void Remove() => _root.RemoveFromHierarchy();
    }
}
