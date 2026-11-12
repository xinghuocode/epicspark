// 历史内容与协议回归：完全离线，使用假 ILlmService，不访问真实模型。
// Unity.exe -batchmode -quit -projectPath <工程> -executeMethod EpicSpark.EditorTools.StoryContentProbe.Run -logFile -
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EpicSpark.Core;
using EpicSpark.Story;
using UnityEditor;
using UnityEngine;

namespace EpicSpark.EditorTools
{
    public static class StoryContentProbe
    {
        private const string ReportPath = "Logs/story-content-report.txt";

        public static void Run()
        {
            var failures = new List<string>();
            var output = new StringBuilder();
            try
            {
                PromptExampleIsValid(failures);
                RemoteAcceptedWithoutNormalization(failures);
                MissingRootBraceIsRepaired(failures);
                UnknownLocalFallbackDoesNotInventHistory(failures);
                SignedAndUnknownYears(failures);
            }
            catch (Exception e)
            {
                failures.Add("探针异常: " + e);
            }

            output.AppendLine("=== 历史内容 / 协议 / 来源追踪验收 ===");
            for (int i = 0; i < failures.Count; i++) output.AppendLine("FAIL " + failures[i]);
            output.AppendLine(failures.Count == 0 ? "结论: PASS" : "结论: FAIL");
            try { System.IO.File.WriteAllText(ReportPath, output.ToString(), new UTF8Encoding(false)); }
            catch (Exception e) { Debug.LogWarning("[StoryContentProbe] 写报告失败: " + e.Message); }
            Debug.Log(output.ToString());
            if (Application.isBatchMode) EditorApplication.Exit(failures.Count == 0 ? 0 : 2);
        }

        private static void PromptExampleIsValid(List<string> failures)
        {
            string prompt = StoryPrompt.SystemPrompt();
            int start = prompt.IndexOf("【输出 JSON 结构】", StringComparison.Ordinal);
            int end = prompt.IndexOf("【ID 规则】", StringComparison.Ordinal);
            Check(failures, start >= 0 && end > start, "提示词包含完整 JSON 示例");
            if (start < 0 || end <= start) return;
            start = prompt.IndexOf('{', start);
            string example = prompt.Substring(start, end - start);
            string error;
            StoryTurn turn = StoryJson.ParseTurn(example, out error);
            Check(failures, turn != null, "提示词中的示例 JSON 自身可解析：" + error);
        }

        private static void RemoteAcceptedWithoutNormalization(List<string> failures)
        {
            string raw = TurnJson("涿鹿之野·有熊聚落", "上古传说时代", "冀北涿鹿之野", -2600);
            var agent = new StoryAgent(new FakeLlm(raw));
            StoryTurn turn = agent.BeginAsync("涿鹿之战", "", CancellationToken.None).GetAwaiter().GetResult();
            Check(failures, turn != null && turn.contentSource == "RemoteAccepted", "任意自然语言事件可直接被远端接受，无需规范事件条目");
            Check(failures, turn != null && turn.scenes[0].region == "冀北涿鹿之野", "远端地域不被替换");
            Check(failures, turn != null && turn.scenes[0].startYear == -2600, "LLM 可直接给出 BCE 年份");
            Check(failures, turn != null && !turn.narrative.Contains("关中"), "远端叙述不包含无关关中模板");
            Check(failures, agent.LastContentSource == "RemoteAccepted", "Agent 明确记录远端接受来源");
        }

        private static void MissingRootBraceIsRepaired(List<string> failures)
        {
            string valid = TurnJson("巴格达南门内广场", "1258年·阿拔斯王朝末期", "两河流域·巴格达", 1258);
            string missingRoot = valid.Substring(0, valid.Length - 1);
            string error;
            StoryTurn parsed = StoryJson.ParseTurn(missingRoot, out error);
            Check(failures, parsed != null && StoryJson.LastUsedRepair, "缺少根对象收尾括号时先修复，不再报没有 JSON：" + error);
            Check(failures, parsed != null && parsed.scenes[0].region.Contains("巴格达"), "修复后保留巴格达历史内容");

            var agent = new StoryAgent(new FakeLlm(missingRoot));
            StoryTurn turn = agent.BeginAsync("蒙古西征军队攻陷巴格达", "", CancellationToken.None).GetAwaiter().GetResult();
            Check(failures, turn != null && turn.contentSource == "RemoteRepaired", "Agent 将修复结果标为 RemoteRepaired");
            Check(failures, turn != null && turn.scenes[0].startYear == 1258, "修复后保留 1258 年，而非西征起点 1219 或 1789");
            Check(failures, turn != null && !turn.scenes[0].sceneName.Contains("关中"), "修复后不再回退关中旧城");
        }

        private static void UnknownLocalFallbackDoesNotInventHistory(List<string> failures)
        {
            var agent = new StoryAgent { EnableRemote = false };
            StoryTurn turn = agent.BeginAsync("涿鹿之战", "【知识库未收录该事件，以下为相近知识】\n蒙古帝国西征", CancellationToken.None)
                .GetAwaiter().GetResult();
            Check(failures, turn != null && turn.contentSource == "LocalFallback", "离线内容明确标为 LocalFallback");
            Check(failures, turn != null && turn.scenes[0].region == "未确认地点", "未知事件不伪造确定地域");
            Check(failures, turn != null && turn.scenes[0].era.Contains("未验证"), "未知事件使用未验证背景");
            Check(failures, turn != null && turn.narrative.Contains("不作为该事件的史实复现"), "本地降级明确声明不是史实复现");
            Check(failures, turn != null && !turn.scenes[0].sceneName.Contains("关中") && !turn.narrative.Contains("关中"),
                "未知事件不再落入关中模板");
            StoryTurn mongol = agent.BeginAsync("蒙古西征军队攻陷巴格达",
                "【知识库事件】蒙古帝国西征\n时期1219—1260年\n地域：蒙古高原/欧亚", CancellationToken.None).GetAwaiter().GetResult();
            Check(failures, mongol != null && mongol.scenes[0].era.Contains("未验证")
                && !mongol.scenes[0].sceneName.Contains("关中"), "大事件知识卡片不能把巴格达子事件归入关中");
            Check(failures, !string.IsNullOrEmpty(agent.LastFallbackReason), "本地兜底原因可追踪");
        }

        private static void SignedAndUnknownYears(List<string> failures)
        {
            GameClock.Reset(-2600);
            Check(failures, GameClock.Display().Contains("公元前2600年"), "BCE 年份以公元前显示");
            GameClock.AddDelta("30min");
            int before = GameClock.BaseYear;
            GameClock.Rebase(-2550);
            Check(failures, GameClock.BaseYear == -2550 && before == -2600 && GameClock.TotalMinutes == 30,
                "换幕校正年份时保留已经过时间");
            GameClock.Reset(0);
            Check(failures, GameClock.Display().Contains("未知年代"), "未知年份不再伪造成 1789");
            GameClock.Reset(1789);
        }

        private static string TurnJson(string sceneName, string era, string region, int startYear)
        {
            return "{\"narrative\":\"这是一段足够长的剧情叙述，明确交代玩家所在地点、当前局势和下一步行动方向。\","
                + "\"gameOver\":false,\"scenes\":[{"
                + "\"sceneName\":\"" + sceneName + "\",\"era\":\"" + era + "\",\"region\":\"" + region + "\","
                + "\"layoutDescription\":\"中央有一片开敞的集会场地，一条土路由南侧入口通往北侧出口，周围保留明确通道与足够活动空间。\","
                + "\"flowDescription\":\"玩家从入口走向集会场地并选择出口。\",\"buildRequirement\":\"保持通道开敞。\","
                + "\"width\":120,\"length\":140,\"startYear\":" + startYear.ToString() + ","
                + "\"anchors\":[{\"displayName\":\"北侧出口\",\"description\":\"走向下一幕\",\"requirement\":\"出口\",\"timeDelta\":\"30min\",\"x\":0,\"z\":40}],"
                + "\"npcs\":[],\"props\":[],\"flow\":[]}]}";
        }

        private static void Check(List<string> failures, bool condition, string message)
        {
            if (!condition) failures.Add(message);
        }

        private sealed class FakeLlm : ILlmService
        {
            private readonly string payload;
            public FakeLlm(string value) { payload = value; }
            public bool HasRemote { get { return true; } }
            public Task<string> CompleteAsync(string systemPrompt, string userPrompt, float temperature, CancellationToken ct)
            {
                return Task.FromResult(payload);
            }
            public Task<LlmToolResponse> CompleteWithToolsAsync(string systemPrompt, string userPrompt,
                IReadOnlyList<LlmToolDef> tools, float temperature, CancellationToken ct)
            {
                return Task.FromResult(new LlmToolResponse { content = payload });
            }
            public Task<float[]> EmbedAsync(string text, CancellationToken ct)
            {
                return Task.FromResult<float[]>(null);
            }
        }
    }
}
