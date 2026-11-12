// 剧情&核心 Agent：优先走远程 LLM（tools / 纯 JSON），任何失败都整体回退到确定性生成。
// 上层只用 BeginAsync / ContinueAsync 两个入口（IStoryAgent），不会感受到远程/本地差异。
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EpicSpark.AI.OpenAI;
using EpicSpark.Core;
using UnityEngine;

namespace EpicSpark.Story
{
    /// <summary>
    /// 剧情&核心 Agent。负责把历史事件推演成可直接施工的 SceneSpec 链。
    /// 远程失败不抛异常：返回确定性兜底回合，并通过 <see cref="LastTurnFromRemote"/> / <see cref="LastError"/> 暴露降级原因。
    /// </summary>
    public class StoryAgent : IStoryAgent
    {
        /// <summary>整条剧情弧的场景数（开端/冲突/转折/高潮/结局）。</summary>
        public const int MaxScenes = DeterministicStoryBuilder.MaxScenes;

        private readonly DeterministicStoryBuilder builder = new DeterministicStoryBuilder();
        private readonly Dictionary<string, int> anchorVisits = new Dictionary<string, int>(StringComparer.Ordinal);
        private ILlmService llm;
        private string eventName = "";
        private string knowledgeContext = "";
        private int lastStageIndex;
        private int remoteFailureLogged;

        /// <summary>
        /// 单幕远端推理的时间预算（秒）。超时立刻放弃这次远端结果、改用确定性剧情继续，
        /// 不会让玩家盯着遮罩干等；推理型模型建议 90 秒以上。
        /// </summary>
        public int RemoteTimeoutSeconds = 90;

        /// <summary>给远端调用套时间预算：超时返回 null，让随后的确定性生成接手。</summary>
        private async Task<StoryTurn> WithRemoteBudgetAsync(Task<StoryTurn> task, string label)
        {
            if (task == null) return null;
            int budget = Mathf.Max(5, RemoteTimeoutSeconds);
            Task winner = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(budget)));
            if (winner != task)
            {
                Debug.LogWarning("[Story] " + label + " 超过 " + budget + " 秒未返回，改用确定性剧情继续。"
                                 + "（推理型模型单幕可能要 60–90 秒，换非推理模型会快很多）");
                return null;
            }
            return await task;
        }

        public StoryAgent()
            : this(null)
        {
        }

        public StoryAgent(ILlmService llm)
        {
            this.llm = llm;
        }

        // ─────────────────────────── 配置 / 诊断 ───────────────────────────

        /// <summary>未显式注入时回落到 Service 容器里的实例。</summary>
        public ILlmService Llm
        {
            get
            {
                if (llm == null) llm = Services.Llm;
                return llm;
            }
            set { llm = value; }
        }

        /// <summary>是否允许调用远程模型。false 时永远走确定性兜底（便于离线测试）。</summary>
        public bool EnableRemote { get; set; } = true;

        /// <summary>是否优先用 OpenAI tools（function calling）；模型不支持时自动退化为纯 JSON 补全。</summary>
        /// <summary>
        /// 是否先走工具调用拿整幕 JSON。默认关闭：
        /// 工具路径要把整幕（含对白、锚点）塞进 tool_calls.arguments，推理型模型经常截断/写错，
        /// 失败后还会再发一次纯 JSON 请求，等于把等待翻倍（实测 83 秒 = 两次完整生成）。
        /// 单次纯 JSON 更快也更稳；需要时可在 Inspector/代码里打开。
        /// </summary>
        public bool EnableTools { get; set; } = false;

        public float Temperature { get; set; } = 0.85f;

        /// <summary>最近一次失败原因（远程失败 / 解析失败 / 兜底触发）。</summary>
        public string LastError { get; private set; }

        /// <summary>最近一个回合是否来自远程模型（false 表示确定性兜底）。</summary>
        public bool LastTurnFromRemote { get; private set; }
        public string LastContentSource { get; private set; } = "";
        public string LastFallbackReason { get; private set; } = "";

        /// <summary>当前事件名（BeginAsync 记录，ContinueAsync 沿用）。</summary>
        public string EventName { get { return eventName; } }

        /// <summary>供调试/测试使用的确定性生成器。</summary>
        public DeterministicStoryBuilder Builder { get { return builder; } }

        // ── 调试录制：最近一次远端请求的提示词 / 原始输出 / 推理过程 ──
        public string LastSystemPrompt { get; private set; }
        public string LastUserPrompt { get; private set; }
        public string LastRawOutput { get; private set; }
        public string LastReasoning { get; private set; }

        // ─────────────────────────── IStoryAgent ───────────────────────────

        public async Task<StoryTurn> BeginAsync(string userEvent, string knowledgeContext, CancellationToken ct)
        {
            eventName = string.IsNullOrEmpty(userEvent) ? "未命名的历史事件" : userEvent.Trim();
            this.knowledgeContext = knowledgeContext ?? "";
            anchorVisits.Clear();
            lastStageIndex = 0;
            LastError = "";
            LastTurnFromRemote = false;
            remoteFailureLogged = 0;

            try
            {
                StoryTurn remoteTurn = null;
                ILlmService service = Llm;
                if (EnableRemote && service != null && service.HasRemote && !ct.IsCancellationRequested)
                {
                    remoteTurn = await WithRemoteBudgetAsync(TryBeginRemoteAsync(service, ct), "第 1 幕远端推理");
                }

                if (remoteTurn != null)
                {
                    LastTurnFromRemote = true;
                    builder.FinalizeRemoteTurn(remoteTurn, eventName, this.knowledgeContext, 0,
                        GameClock.TotalMinutes, false);
                    EnsureNarrative(remoteTurn, eventName, StoryStage.Opening);
                    remoteTurn.activeSceneIndex = Mathf.Clamp(remoteTurn.activeSceneIndex, 0, remoteTurn.scenes.Count - 1);
                    Debug.Log("[Story] 远程生成第 1 幕成功，场景 " + remoteTurn.scenes.Count + " 个，来源 "
                              + (StoryJson.LastUsedRepair ? "RemoteRepaired" : "RemoteAccepted") + "。");
                    return MarkRemote(remoteTurn);
                }

                StoryTurn turn = builder.BuildBeginTurn(eventName, this.knowledgeContext, GameClock.TotalMinutes);
                turn.activeSceneIndex = 0;
                Debug.Log("[Story] 使用确定性兜底生成第 1 幕（" + eventName + "）：" + DescribeFirstScene(turn));
                return MarkLocal(turn);
            }
            catch (Exception e)
            {
                LastError = e.GetType().Name + ": " + e.Message;
                Debug.LogWarning("[Story] BeginAsync 异常，改用最简场景: " + e);
                return SafeBeginFallback();
            }
        }

        public async Task<StoryTurn> ContinueAsync(StoryContinueRequest request, CancellationToken ct)
        {
            if (request == null) request = new StoryContinueRequest();
            if (string.IsNullOrEmpty(eventName))
                eventName = string.IsNullOrEmpty(request.currentSceneName) ? "未命名的历史事件" : request.currentSceneName;

            int visitCount = BumpVisitAnchor(request);
            int stageIndex = ResolveNextIndex(request);
            StoryStage stage = builder.StageForIndex(stageIndex);
            bool isEnding = stageIndex >= MaxScenes - 1;
            LastError = "";
            LastTurnFromRemote = false;

            try
            {
                StoryTurn remoteTurn = null;
                ILlmService service = Llm;
                if (EnableRemote && service != null && service.HasRemote && !ct.IsCancellationRequested)
                {
                    remoteTurn = await WithRemoteBudgetAsync(TryContinueRemoteAsync(service, request, stageIndex, stage, isEnding, ct), "第 " + (stageIndex + 1) + " 幕远端推理");
                }

                if (remoteTurn != null)
                {
                    LastTurnFromRemote = true;
                    builder.FinalizeRemoteTurn(remoteTurn, eventName, knowledgeContext, stageIndex,
                        GameClock.TotalMinutes, isEnding);
                    EnsureNarrative(remoteTurn, eventName, stage);
                    remoteTurn.activeSceneIndex = Mathf.Clamp(remoteTurn.activeSceneIndex, 0, remoteTurn.scenes.Count - 1);
                    lastStageIndex = stageIndex;
                    Debug.Log("[Story] 远程生成第 " + (stageIndex + 1) + " 幕（" + builder.DescribeStage(stage) + "）成功，来源 "
                              + (StoryJson.LastUsedRepair ? "RemoteRepaired" : "RemoteAccepted") + "。");
                    return MarkRemote(remoteTurn);
                }

                StoryTurn turn = builder.BuildContinueTurn(eventName, knowledgeContext, request, stageIndex, visitCount,
                    GameClock.TotalMinutes);
                lastStageIndex = stageIndex;
                Debug.Log("[Story] 确定性推进到第 " + (stageIndex + 1) + " 幕（" + builder.DescribeStage(stage) + "）："
                    + DescribeFirstScene(turn) + (turn.gameOver ? "（结局）" : ""));
                return MarkLocal(turn);
            }
            catch (Exception e)
            {
                LastError = e.GetType().Name + ": " + e.Message;
                Debug.LogWarning("[Story] ContinueAsync 异常，改用最简场景: " + e);
                lastStageIndex = stageIndex;
                return SafeContinueFallback();
            }
        }

        // ─────────────────────────── 远程路径 ───────────────────────────

        private async Task<StoryTurn> TryBeginRemoteAsync(ILlmService service, CancellationToken ct)
        {
            try
            {
                string system = StoryPrompt.SystemPrompt();
                string user = StoryPrompt.BeginUserPrompt(eventName, knowledgeContext);
                return await RequestTurnAsync(service, system, user, ct);
            }
            catch (Exception e)
            {
                NoteRemoteFailure("BeginAsync 远程异常: " + e.Message);
                return null;
            }
        }

        private async Task<StoryTurn> TryContinueRemoteAsync(
            ILlmService service,
            StoryContinueRequest request,
            int stageIndex,
            StoryStage stage,
            bool isEnding,
            CancellationToken ct)
        {
            try
            {
                string system = StoryPrompt.SystemPrompt();
                string user = StoryPrompt.ContinueUserPrompt(request, eventName, knowledgeContext, stageIndex,
                    builder.DescribeStage(stage), isEnding);
                return await RequestTurnAsync(service, system, user, ct);
            }
            catch (Exception e)
            {
                NoteRemoteFailure("ContinueAsync 远程异常: " + e.Message);
                return null;
            }
        }

        /// <summary>先试 tools，再试纯 JSON 补全；两次都失败返回 null。</summary>
        private async Task<StoryTurn> RequestTurnAsync(
            ILlmService service,
            string systemPrompt,
            string userPrompt,
            CancellationToken ct)
        {
            LastSystemPrompt = systemPrompt ?? "";
            LastUserPrompt = userPrompt ?? "";
            LastRawOutput = "";
            LastReasoning = "";
            if (EnableTools)
            {
                try
                {
                    var tools = new List<LlmToolDef> { StoryPrompt.BuildToolDef() };
                    LlmToolResponse response = await service.CompleteWithToolsAsync(systemPrompt, userPrompt, tools,
                        Temperature, ct);

                    if (response != null && !response.HasError)
                    {
                        for (int i = 0; i < response.calls.Count; i++)
                        {
                            LlmToolCall call = response.calls[i];
                            if (call == null || string.IsNullOrEmpty(call.argumentsJson)) continue;
                            string parseError;
                            StoryTurn turn = ParseAndValidate(call.argumentsJson, out parseError);
                            if (turn != null) return turn;
                            NoteRemoteFailure("工具参数不可用（" + parseError + "）");
                        }

                        if (!string.IsNullOrEmpty(response.content))
                        {
                            string parseError;
                            StoryTurn turn = ParseAndValidate(response.content, out parseError);
                            if (turn != null) return turn;
                            NoteRemoteFailure("工具响应内容不可用（" + parseError + "）");
                        }
                    }
                    else if (response != null)
                    {
                        NoteRemoteFailure("工具调用失败：" + response.error);
                    }
                }
                catch (Exception e)
                {
                    NoteRemoteFailure("工具调用异常: " + e.Message);
                }
            }

            // 退化为：直接要求返回 JSON 的单轮补全
            try
            {
                string text = await service.CompleteAsync(systemPrompt, userPrompt, Temperature, ct);
                LastRawOutput = text ?? "";
                LastReasoning = OpenAIClient.LastReasoning ?? "";
                if (!string.IsNullOrEmpty(text))
                {
                    string parseError;
                    StoryTurn turn = ParseAndValidate(text, out parseError);
                    if (turn != null) return turn;
                    NoteRemoteFailure("补全内容不可用（" + parseError + "）");
                }
                else
                {
                    NoteRemoteFailure("补全返回空内容");
                }
            }
            catch (Exception e)
            {
                NoteRemoteFailure("补全异常: " + e.Message);
            }
            return null;
        }

        /// <summary>解析并做最低限度的质量校验；不合格就整体回退。</summary>
        private StoryTurn ParseAndValidate(string raw, out string error)
        {
            StoryTurn turn = StoryJson.ParseTurn(raw, out error);
            if (turn == null) return null;

            SceneSpec first = turn.scenes[0];
            if (first.anchors == null || first.anchors.Count == 0)
            {
                error = "首个场景没有锚点，玩家会卡死";
                return null;
            }
            if (string.IsNullOrEmpty(first.layoutDescription) || first.layoutDescription.Length < 40)
            {
                error = "布局描述过短，无法复现";
                return null;
            }
            if (string.IsNullOrEmpty(turn.narrative) || turn.narrative.Length < 20)
            {
                error = "叙述文字过短";
                return null;
            }

            // 结构化语义只是可选消歧提示；详细 layoutDescription 才是必需需求。
            // 两种输入都进入同一个 GrayboxToolWorkspace，不存在质量较低的“无规格旁路”。
            int withSpec = 0;
            for (int i = 0; i < turn.scenes.Count; i++)
            {
                SceneSpec scene = turn.scenes[i];
                if (scene == null) continue;
                if (scene.layoutSpec != null && !scene.layoutSpec.Usable) scene.layoutSpec = null;
                if (scene.layoutSpec != null) withSpec++;
            }
            if (withSpec == 0)
                Debug.Log("[Story] 本幕使用详细自然语言需求，由灰盒 Agent 调用工具理解并校验。");
            error = "";
            return turn;
        }

        private void NoteRemoteFailure(string message)
        {
            LastError = message;
            remoteFailureLogged++;
            if (remoteFailureLogged <= 3)
                Debug.LogWarning("[Story] 远程不可用，改走确定性兜底: " + message);
        }

        private StoryTurn MarkRemote(StoryTurn turn)
        {
            LastContentSource = StoryJson.LastUsedRepair ? "RemoteRepaired" : "RemoteAccepted";
            LastFallbackReason = "";
            if (turn != null)
            {
                turn.contentSource = LastContentSource;
                turn.fallbackReason = "";
            }
            return turn;
        }

        private StoryTurn MarkLocal(StoryTurn turn)
        {
            LastContentSource = turn != null ? "LocalFallback" : "";
            LastFallbackReason = string.IsNullOrEmpty(LastError) ? "未配置或未使用远端剧情模型" : LastError;
            if (turn != null)
            {
                turn.contentSource = LastContentSource;
                turn.fallbackReason = LastFallbackReason;
            }
            return turn;
        }

        // ─────────────────────────── 兜底与收尾 ───────────────────────────

        private int ResolveNextIndex(StoryContinueRequest request)
        {
            int parsed = builder.ParseSceneIndex(request.currentSceneId);
            int next;
            if (parsed >= 0) next = parsed + 1;
            else next = lastStageIndex + 1;
            if (next < 1) next = 1;
            if (next > MaxScenes - 1) next = MaxScenes - 1;
            return next;
        }

        private int BumpVisitAnchor(StoryContinueRequest request)
        {
            string key = !string.IsNullOrEmpty(request.anchorId)
                ? request.anchorId
                : (request.anchorName ?? "");
            if (string.IsNullOrEmpty(key)) key = "_default_" + request.currentSceneId;
            int count;
            anchorVisits.TryGetValue(key, out count);
            count++;
            anchorVisits[key] = count;
            return count;
        }

        /// <summary>远程回合的叙述为空时补一段，避免 HUD 空白。</summary>
        private void EnsureNarrative(StoryTurn turn, string evtName, StoryStage stage)
        {
            if (turn == null) return;
            if (!string.IsNullOrEmpty(turn.narrative)) return;
            string sceneName = turn.scenes.Count > 0 ? turn.scenes[0].sceneName : "未知的场景";
            turn.narrative = "【" + evtName + "·" + builder.DescribeStage(stage) + "】你来到" + sceneName
                + "。四周的动静说明局势已经变了，你需要尽快判断该往哪个方向走。";
        }

        private StoryTurn SafeBeginFallback()
        {
            try
            {
                StoryTurn turn = builder.BuildBeginTurn(eventName, knowledgeContext, GameClock.TotalMinutes);
                return MarkLocal(turn);
            }
            catch (Exception inner)
            {
                Debug.LogError("[Story] 确定性开局也失败: " + inner);
                return MarkLocal(MinimalTurn(0));
            }
        }

        private StoryTurn SafeContinueFallback()
        {
            try
            {
                var request = new StoryContinueRequest { currentSceneId = "", currentSceneName = "" };
                StoryTurn turn = builder.BuildContinueTurn(eventName, knowledgeContext, request,
                    Mathf.Clamp(lastStageIndex, 1, MaxScenes - 1), 1, GameClock.TotalMinutes);
                return MarkLocal(turn);
            }
            catch (Exception inner)
            {
                Debug.LogError("[Story] 确定性推进也失败: " + inner);
                return MarkLocal(MinimalTurn(Mathf.Clamp(lastStageIndex + 1, 1, MaxScenes - 1)));
            }
        }

        /// <summary>最后一道防线：绝不返回 null，保证上层不会因为剧情生成失败而崩。</summary>
        private StoryTurn MinimalTurn(int index)
        {
            var scene = new SceneSpec
            {
                sceneId = "s_safe_" + index + "_a",
                sceneName = "临时场景（生成失败）",
                era = "未知",
                region = "未知",
                layoutDescription = "60×60 米空地（X、Z 均为 -30…+30），地面为夯土。出生点 (0, -24)，"
                    + "中心 (0, 0) 立着一块石碑（2×2 米），南侧 (0, -27) 停着一辆马车，马车前 1.5 米为可触发范围。",
                flowDescription = "1) 玩家在 (0, -24) 出生，沿 X=0 向北走到 (0, 0) 的石碑前。"
                    + "2) 玩家走向 (0, -27) 的马车并踏入 1.5 米范围，时间推进 1 小时，进入下一幕。",
                buildRequirement = "只需空地、石碑与马车三件地物；坐标单位为米。",
                width = 60f,
                length = 60f,
                spawnPoint = new Vector3(0f, 1.2f, -24f),
            };
            scene.anchors.Add(new SceneAnchorSpec
            {
                anchorId = "a_safe_" + index,
                displayName = "离场的马车",
                description = "停在 (0, -27) 的马车，车夫在车辕上等着。",
                requirement = "必须是可登乘的马车车身（约 4 米长、1.8 米宽）",
                modelHint = "wooden carriage wagon horse",
                timeDelta = "1h",
                targetSceneId = "",
                targetSceneName = "",
                localPosition = new Vector3(0f, 0f, -27f),
            });
            scene.npcs.Add(new NpcSpec
            {
                npcId = "n_safe_" + index,
                displayName = "路过的市民",
                tag = "平民",
                persona = "普通市民，说话谨慎，只想尽快离开这里。",
                maxHp = 100f,
                localPosition = new Vector3(-4f, 0f, -8f),
            });
            scene.props.Add(new PropInteraction
            {
                propId = "prop_safe",
                displayName = "石碑",
                description = "碑文模糊的石碑。",
                interactKind = "examine",
                effect = "查看后得到一句含糊的记载。",
            });
            scene.flow.Add(new FlowStep
            {
                stepId = "f_safe_1",
                actorId = "player",
                regionId = "square",
                description = "玩家在 (0, -24) 出生，向北走到 (0, 0) 的石碑前。",
                trigger = "场景加载完成",
                effect = "本幕开始",
                delaySeconds = 0f,
            });

            var turn = new StoryTurn
            {
                turnId = "t_safe_" + index,
                narrative = "【" + eventName + "】你在一片空地上醒来，只有一块石碑和一辆马车。",
                activeSceneIndex = 0,
                gameOver = index >= MaxScenes - 1,
                endingSummary = index >= MaxScenes - 1 ? "剧情在临时场景中收束。" : "",
                endingAchievement = index >= MaxScenes - 1 ? "【见证者】走完全程" : "",
            };
            turn.scenes.Add(scene);
            return turn;
        }

        private static string DescribeFirstScene(StoryTurn turn)
        {
            if (turn == null || turn.scenes.Count == 0) return "（无场景）";
            SceneSpec scene = turn.scenes[0];
            return scene.sceneName + "｜锚点 " + scene.anchors.Count + "｜NPC " + scene.npcs.Count
                + "｜道具 " + scene.props.Count + "｜流程步 " + scene.flow.Count;
        }
    }
}
