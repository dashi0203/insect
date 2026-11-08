using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace Swarm.Tests
{
    /// <summary>
    /// 不关闭编辑器即可从外部跑 EditMode 测试：创建 Temp/swarm_run_tests 文件，
    /// 编辑器空闲时运行 Swarm.Tests.EditMode，并把结果写到 Temp/swarm_test_results.txt。
    /// </summary>
    [InitializeOnLoad]
    static class TestTriggerHook
    {
        const string TriggerPath = "Temp/swarm_run_tests";
        const string ResultPath = "Temp/swarm_test_results.txt";
        const string RefreshedPath = "Temp/swarm_tests_refreshed";
        static double s_nextPoll;

        static TestTriggerHook() => EditorApplication.update += Poll;

        static void Poll()
        {
            if (EditorApplication.timeSinceStartup < s_nextPoll) return;
            s_nextPoll = EditorApplication.timeSinceStartup + 2.0;
            if (!File.Exists(TriggerPath)) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                File.WriteAllText(ResultPath, "BLOCKED: editor is in play mode\n");
                return;
            }
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            // 先刷新资源：外部改过的脚本会在这里触发编译 + 域重载，重载后下一次轮询再跑测试
            if (!File.Exists(RefreshedPath))
            {
                File.WriteAllText(RefreshedPath, "");
                AssetDatabase.Refresh();
                return;
            }
            File.Delete(RefreshedPath);
            File.Delete(TriggerPath);
            File.WriteAllText(ResultPath, "RUNNING\n");

            var api = ScriptableObject.CreateInstance<TestRunnerApi>();
            api.RegisterCallbacks(new Callbacks());
            api.Execute(new ExecutionSettings(new Filter
            {
                testMode = TestMode.EditMode,
                assemblyNames = new[] { "Swarm.Tests.EditMode" },
            }));
        }

        sealed class Callbacks : ICallbacks
        {
            public void RunStarted(ITestAdaptor testsToRun) { }
            public void TestStarted(ITestAdaptor test) { }
            public void TestFinished(ITestResultAdaptor result) { }

            public void RunFinished(ITestResultAdaptor result)
            {
                var sb = new StringBuilder();
                sb.AppendLine($"DONE {result.TestStatus}  passed {result.PassCount}  failed {result.FailCount}  " +
                              $"skipped {result.SkipCount}  {result.Duration:0.0}s");
                Collect(result, sb);
                File.WriteAllText(ResultPath, sb.ToString());
            }

            static void Collect(ITestResultAdaptor r, StringBuilder sb)
            {
                if (!r.HasChildren)
                {
                    sb.AppendLine($"{r.TestStatus,-8} {r.Duration,6:0.00}s  {r.FullName}");
                    if (r.TestStatus == TestStatus.Failed) sb.AppendLine("    " + r.Message?.Trim());
                    return;
                }
                foreach (var c in r.Children) Collect(c, sb);
            }
        }
    }
}
