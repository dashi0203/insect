using System;
using System.Collections.Generic;
using System.Text;

namespace Swarm.UI
{
    /// <summary>调试面板的文字来源注册表：各系统注册一个返回文字的函数，DebugPanel 按注册顺序拼接。</summary>
    public static class DebugInfo
    {
        static readonly List<Func<string>> s_sources = new List<Func<string>>();
        static readonly StringBuilder s_sb = new StringBuilder();

        public static void Register(Func<string> source)
        {
            if (source != null && !s_sources.Contains(source)) s_sources.Add(source);
        }

        public static void Unregister(Func<string> source) => s_sources.Remove(source);

        public static string Collect()
        {
            s_sb.Clear();
            foreach (var s in s_sources)
            {
                string text = s();
                if (string.IsNullOrEmpty(text)) continue;
                s_sb.Append("\n\n").Append(text);
            }
            return s_sb.ToString();
        }
    }
}
