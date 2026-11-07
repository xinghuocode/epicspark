using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EpicSpark.AI.OpenAI;
using EpicSpark.AI.SceneAgentTest;
using UnityEngine;

namespace EpicSpark.AI.StoryAgent
{
    /// <summary>
    /// 剧情Agent的决策结果。
    /// 当前阶段(generate_scene 未接入)剧情Agent只能: enter_scene 进入一个预置场景, 或 finish 结束。
    /// </summary>
    public class PlotDecision
    {
        /// <summary>选中的预置场景(enter_scene 的 name 命中目录时给出)。</summary>
        public PlotScenePreset preset;

        /// <summary>enter_scene 参数原样记录(ARCHITECT §4.1)。</summary>
        public string enterName = "";
        public bool isStillInPlace;
        public bool isReplaceModel;

        /// <summary>true 表示剧情Agent调用了 finish(游戏结束)。</summary>
        public bool gameOver;
        public string finishReason = "";
        public string finishSummary = "";
        public string finishAchievement = "";

        /// <summary>由预置定义推导场景参数(供场景Agent测试床使用, 非 LLM 创作)。</summary>
        public string ToGenerateSceneJson()
        {
            if (preset == null) return "";
            var anchorList = new List<object>();
            foreach (var a in preset.anchors)
            {
                anchorList.Add(new Dictionary<string, object>
                {
                    ["name"] = a.name,
                    ["description"] = a.description,
                    ["requirement"] = a.requirement,
                    ["spend_time"] = a.spendTime,
                });
            }
            return MiniJson.Serialize(new Dictionary<string, object>
            {
                ["scene_name"] = preset.name,
                ["scene_description"] = preset.description,
                ["requirement"] = preset.requirementHints,
                ["transmission_auchor_points"] = anchorList,
                ["width"] = preset.defaultWidth,
                ["length"] = preset.defaultLength,
            });
        }
    }

    /// <summary>
    /// 剧情Agent (ARCHITECT §4.1 的前期形态, generate_scene 暂未接入)。
    /// 可用工具严格按文档: enter_scene / finish。
    /// 系统提示词中列出预置场景目录, 剧情Agent通过 enter_scene(name=预置场景名)完成选择。
    /// 工具由本类本地执行(校验+登记), 不触发任何场景生成。
    /// </summary>
    public class PlotAgent
    {
        public const string EnterSceneTool = "enter_scene";
        public const string FinishTool = "finish";
        public int MaxRounds = 8;

        /// <summary>当前场景名(本地状态; 开局为空)。is_still_in_place/is_replace_model 的校验依赖它。</summary>
        public string CurrentSceneName = "";

        /// <summary>最近一轮 LLM 的原始输出(调试)。</summary>
        public string LastRawOutput = "";
        public string LastError = "";

        // ================== 系统提示词 ==================

        public static string BuildSystemPrompt() => BuildSystemPrompt(PlotScenePresets.All);

        public static string BuildSystemPrompt(IReadOnlyList<PlotScenePreset> catalog)
        {
            return
                "你是 EpicSpark 游戏的剧情Agent (ARCHITECT §4.1)。用户会输入一个历史事件名称和该事件的相关知识信息。\n" +
                "你的任务: 从预置场景目录中为该历史事件选择一个场景让玩家进入, 开启这段历史体验。\n" +
                "\n" +
                BuildCatalogText(catalog) +
                "\n" +
                "你可用的工具(严格遵守参数定义):\n" +
                "1. enter_scene —— 进入指定场景:\n" +
                "   - name (string): 要进入的场景名称。必须严格等于目录中某个预置场景的名称;\n" +
                "   - is_still_in_place (bool): 是否在原地。只有 name 为当前场景的名称时这个参数才有意义(原地切换);\n" +
                "   - is_replace_model (bool): 是否是替换建模。true 表示将用户交互的传送锚点位置替换为新的场景模型," +
                "用于室内外等效果; 当用户离开该场景进入外界时, 恢复原模型。\n" +
                "2. finish —— 结束游戏:\n" +
                "   - reason (string): 结束原因, 用于游戏结束动画;\n" +
                "   - summary (string): 游戏总结, 用于游戏结束动画;\n" +
                "   - achievement (string): 成就, 用于游戏结束动画和开始页展示。\n" +
                "\n" +
                "规则:\n" +
                "- 开局时调用 enter_scene 进入你选择的预置场景(此时没有当前场景, is_still_in_place 应为 false);\n" +
                "- 一次回复只发起一轮工具调用; 若工具返回 ERROR, 修正参数后重试, 不要放弃;\n" +
                "- 只有当你认为这个游戏不应继续时才调用 finish。";
        }

        // ================== 工具定义(严格按 ARCHITECT §4.1) ==================

        public static ToolDefinition[] Tools =>
            new[]
            {
                new ToolDefinition
                {
                    function = new ToolFunctionInfo
                    {
                        name = EnterSceneTool,
                        description = "进入指定场景。在用户进入传送锚点后唤醒剧情Agent, 直到剧情Agent调用此工具, 用户才会进入指定场景。",
                        parameters =
                            "{\"type\":\"object\",\"properties\":{" +
                            "\"name\":{\"type\":\"string\",\"description\":\"要进入的场景名称\"}," +
                            "\"is_still_in_place\":{\"type\":\"boolean\",\"description\":\"是否在原地。只有name为当前场景的name这个参数才有意义\"}," +
                            "\"is_replace_model\":{\"type\":\"boolean\",\"description\":\"是否是替换建模。true则将用户交互的传送锚点位置替换为新的场景模型, 实现室内外等效果; 离开时恢复原模型\"}" +
                            "},\"required\":[\"name\",\"is_still_in_place\",\"is_replace_model\"]}",
                    },
                },
                new ToolDefinition
                {
                    function = new ToolFunctionInfo
                    {
                        name = FinishTool,
                        description = "结束游戏。当剧情Agent认为游戏已经结束时调用。触发游戏结束动画等。",
                        parameters =
                            "{\"type\":\"object\",\"properties\":{" +
                            "\"reason\":{\"type\":\"string\",\"description\":\"结束原因。用于游戏结束动画等\"}," +
                            "\"summary\":{\"type\":\"string\",\"description\":\"游戏总结。用于游戏结束动画等\"}," +
                            "\"achievement\":{\"type\":\"string\",\"description\":\"成就。用于游戏结束动画和开始页展示等\"}" +
                            "},\"required\":[\"reason\",\"summary\",\"achievement\"]}",
                    },
                },
            };

        // ================== 主流程 ==================

        /// <summary>剧情Agent主流程(默认目录=内置预置场景)。</summary>
        public Task<PlotDecision> BeginAsync(OpenAIClient client, string userEvent, string knowledgeContext,
            Action<string> onProgress, CancellationToken ct)
            => BeginAsync(client, userEvent, knowledgeContext, PlotScenePresets.All, onProgress, ct);

        /// <summary>剧情Agent主流程: 输入历史事件名+知识, 经工具调用循环得到决策。失败返回 null(见 LastError)。</summary>
        public async Task<PlotDecision> BeginAsync(OpenAIClient client, string userEvent, string knowledgeContext,
            IReadOnlyList<PlotScenePreset> catalog, Action<string> onProgress, CancellationToken ct)
        {
            LastError = "";
            LastRawOutput = "";
            if (client == null) { LastError = "OpenAIClient 为空"; return null; }
            if (string.IsNullOrWhiteSpace(userEvent)) { LastError = "历史事件名为空"; return null; }

            var messages = new List<ChatMessage>
            {
                ChatMessage.System(BuildSystemPrompt(catalog)),
                ChatMessage.User(BuildUserInput(userEvent, knowledgeContext)),
            };
            var options = new ChatOptions { tools = Tools, toolChoice = "auto" };

            for (int round = 1; round <= MaxRounds; round++)
            {
                ct.ThrowIfCancellationRequested();
                onProgress?.Invoke($"剧情Agent 第 {round} 轮…");

                ChatResponse resp = await client.ChatAsync(messages, options: options, cancellationToken: ct);
                var msg = resp.choices?[0]?.message;
                messages.Add(new ChatMessage { role = "assistant", content = msg?.content ?? "", tool_calls = msg?.tool_calls });
                LastRawOutput = msg?.content ?? "";

                var toolCalls = msg?.tool_calls;
                if (toolCalls == null || toolCalls.Length == 0)
                {
                    messages.Add(ChatMessage.User("请调用 enter_scene 选择并进入预置场景(或 finish 结束), 不要只输出文字。"));
                    continue;
                }

                foreach (var tc in toolCalls)
                {
                    string tool = tc.function?.name ?? "";
                    Dictionary<string, object> args;
                    try
                    {
                        args = MiniJson.AsDict(MiniJson.Parse(tc.function?.arguments ?? "{}")) ?? new Dictionary<string, object>();
                    }
                    catch (Exception e)
                    {
                        messages.Add(ChatMessage.Tool(tc.id, "ERROR: 参数不是合法 JSON: " + e.Message));
                        continue;
                    }

                    string result = Execute(tool, args, catalog, out PlotDecision decision);
                    onProgress?.Invoke($"[工具 {tool}] {Shorten(result, 300)}");
                    messages.Add(ChatMessage.Tool(tc.id, result));

                    if (decision != null) return decision; // enter_scene 成功或 finish
                }
            }

            LastError = "达到最大轮数仍未做出合法决策";
            return null;
        }

        // ================== 本地工具执行(严格按文档语义校验) ==================

        static string BuildUserInput(string userEvent, string knowledgeContext)
        {
            var sb = new StringBuilder();
            sb.AppendLine("用户输入的历史事件名称: " + userEvent);
            if (!string.IsNullOrWhiteSpace(knowledgeContext))
            {
                sb.AppendLine("该事件的相关知识信息:");
                sb.AppendLine(knowledgeContext);
            }
            else
            {
                sb.AppendLine("(该事件暂无额外知识信息, 请依据你的历史知识进行判断。)");
            }
            sb.AppendLine("请调用 enter_scene 从预置场景目录中选择一个进入。");
            return sb.ToString();
        }

        static string BuildCatalogText(IReadOnlyList<PlotScenePreset> catalog)
        {
            var sb = new StringBuilder();
            sb.AppendLine("你可使用的预置场景目录(只能从中选择一个):");
            foreach (var p in catalog)
            {
                sb.AppendLine($"- 名称: {p.name}");
                sb.AppendLine($"  简介: {p.description}");
                sb.AppendLine($"  支持的传送锚点:");
                foreach (var a in p.anchors)
                    sb.AppendLine($"    * {a.name} | {a.description} | 要求: {a.requirement} | 时间: {a.spendTime}");
            }
            return sb.ToString();
        }

        /// <summary>执行 enter_scene / finish。成功(enter_scene 校验通过 / finish)返回 OK 并给 decision。</summary>
        string Execute(string tool, Dictionary<string, object> args, IReadOnlyList<PlotScenePreset> catalog, out PlotDecision decision)
        {
            decision = null;

            if (tool == EnterSceneTool)
            {
                string name = MiniJson.GetString(args, "name") ?? "";
                bool still = GetBool(args, "is_still_in_place");
                bool replace = GetBool(args, "is_replace_model");

                PlotScenePreset preset = null;
                if (catalog != null)
                    foreach (var p in catalog)
                        if (p.name == name.Trim()) { preset = p; break; }
                if (preset == null)
                {
                    var names = new StringBuilder();
                    if (catalog != null) foreach (var p in catalog) names.Append(p.name).Append(" / ");
                    return $"ERROR: 场景 '{name}' 不存在于预置场景目录。可选: {names.ToString().TrimEnd(' ', '/')}";
                }

                bool isCurrent = !string.IsNullOrEmpty(CurrentSceneName) && name == CurrentSceneName;

                // 文档语义: is_still_in_place 只有 name 为当前场景的 name 时才有意义
                if (still && !isCurrent)
                    return $"ERROR: is_still_in_place 只有 name 为当前场景名称时才有意义。当前场景为" +
                           (string.IsNullOrEmpty(CurrentSceneName) ? "(空)" : $"'{CurrentSceneName}'") +
                           $", 而你要进入 '{name}'(新场景), 请将其设为 false。";

                // 替换建模是"原地把锚点模型替换为新场景模型"的语义, 必须原地
                if (replace && !still)
                    return "ERROR: is_replace_model 为 true 时必须同时在原地(is_still_in_place=true, 且 name 为当前场景名称)。";

                CurrentSceneName = name;
                decision = new PlotDecision
                {
                    preset = preset,
                    enterName = name,
                    isStillInPlace = still,
                    isReplaceModel = replace,
                };
                return $"OK: 进入场景「{name}」。" +
                       (replace ? "(替换建模: 锚点位置将替换为新场景模型, 离开时恢复)" : "") +
                       (still ? "(原地)" : "");
            }

            if (tool == FinishTool)
            {
                decision = new PlotDecision
                {
                    gameOver = true,
                    finishReason = MiniJson.GetString(args, "reason", ""),
                    finishSummary = MiniJson.GetString(args, "summary", ""),
                    finishAchievement = MiniJson.GetString(args, "achievement", ""),
                };
                return "OK: 游戏结束。" +
                       (string.IsNullOrEmpty(decision.finishReason) ? "" : $" 原因: {decision.finishReason}") +
                       (string.IsNullOrEmpty(decision.finishSummary) ? "" : $" 总结: {decision.finishSummary}") +
                       (string.IsNullOrEmpty(decision.finishAchievement) ? "" : $" 成就: {decision.finishAchievement}");
            }

            return $"ERROR: 未知工具 '{tool}'。可用工具: {EnterSceneTool} / {FinishTool}。";
        }

        static bool GetBool(Dictionary<string, object> args, string key)
        {
            if (args == null) return false;
            if (!args.TryGetValue(key, out var v) || v == null) return false;
            if (v is bool b) return b;
            return string.Equals(v as string, "true", StringComparison.OrdinalIgnoreCase);
        }

        static string Shorten(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\n", " ");
            return s.Length > max ? s.Substring(0, max) + "…" : s;
        }
    }
}
