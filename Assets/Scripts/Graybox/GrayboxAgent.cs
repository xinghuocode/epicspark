// 灰盒 Agent：把 SceneSpec 的文字描述翻译成灰盒空间骨架。
// 两条路径：
//   1) 远端：通过 Services.Llm.CompleteWithToolsAsync 多轮调用受限的灰盒编辑工具，解析 argumentsJson 填充 GrayboxPlan；
//   2) 兜底：走 GrayboxPlanner 的确定性规划（任何一步失败都整体回退，绝不抛异常）。
// 远端结果同样要过 Sanitize（坐标夹紧/多边形闭合/尺寸 clamp）+ CompleteBindings（补齐 NPC/锚点/道具绑定）+ Validate。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using EpicSpark.Core;
using UnityEngine;

namespace EpicSpark.Graybox
{
    /// <summary>IGrayboxAgent 实现：LLM 工具调用优先，确定性规划兜底。</summary>
    public class GrayboxAgent : IGrayboxAgent
    {
        /// <summary>远端工具调用最大轮数（防止模型无限循环）。</summary>
        public const int MaxToolRounds = 10;

        /// <summary>
        /// 远端规划的时间预算（秒）。超时立刻放弃远端、用确定性规划继续：
        /// 灰盒只是空间编排，不值得为它等好几分钟。
        /// </summary>
        public int RemoteTimeoutSeconds = 150;

        /// <summary>
        /// 远端工具调用轮数上限。每一轮都是一次完整的模型请求（推理模型一轮可能 60 秒），
        /// 默认 2 轮，够模型"铺主要结构 → 收尾"，又不会把一次开局拖成几分钟。
        /// </summary>
        public int ToolRounds = 2;

        private readonly ILlmService llm;
        private readonly GrayboxPlanner planner;

        public GrayboxAgent()
            : this(null)
        {
        }

        /// <summary>注入 LLM 服务；为 null 时运行时读取 Services.Llm。</summary>
        public GrayboxAgent(ILlmService llmService)
        {
            llm = llmService;
            planner = new GrayboxPlanner();
        }

        /// <summary>确定性规划器（Bootstrap / 测试可直接调用做离线验证）。</summary>
        public GrayboxPlanner Planner
        {
            get { return planner; }
        }

        /// <summary>给远端调用套时间预算：超时返回 null，让随后的确定性规划接手（异常仍向上抛，由 PlanAsync 统一兜底）。</summary>
        private async Task<GrayboxPlan> WithRemoteBudgetAsync(Task<GrayboxPlan> task, string label)
        {
            if (task == null) return null;
            int budget = Mathf.Max(5, RemoteTimeoutSeconds);
            Task winner = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(budget)));
            if (winner != task)
            {
                Debug.LogWarning("[灰盒] " + label + " 超过 " + budget + " 秒未返回，改用确定性规划继续。"
                                 + "（推理型模型单次可能要 60 秒以上，换非推理模型会快很多）");
                return null;
            }
            return await task;
        }

        public async Task<GrayboxPlan> PlanAsync(GrayboxRequest request, CancellationToken ct)
        {
            if (request == null) request = new GrayboxRequest { scene = new SceneSpec(), seed = 1 };
            if (request.scene == null) request.scene = new SceneSpec();
            ct.ThrowIfCancellationRequested();

            ILlmService service = llm != null ? llm : Services.Llm;
            if (service != null && service.HasRemote)
            {
                GrayboxPlan remotePlan = null;
                try
                {
                    remotePlan = await WithRemoteBudgetAsync(PlanRemoteAsync(request, service, ct), "灰盒规划");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[灰盒] 远端灰盒规划异常，整体回退确定性规划: " + e.Message);
                    remotePlan = null;
                }

                if (remotePlan != null)
                {
                    if (remotePlan.width <= 1f) remotePlan.width = request.scene.width > 1f ? request.scene.width : 60f;
                    if (remotePlan.length <= 1f) remotePlan.length = request.scene.length > 1f ? request.scene.length : 60f;
                    GrayboxPlanner.Sanitize(remotePlan, request.scene);
                    planner.CompleteBindings(remotePlan, request.scene);
                    GrayboxPlanner.Sanitize(remotePlan, request.scene);

                    string reason;
                    if (GrayboxPlanner.Validate(remotePlan, request.scene, out reason))
                    {
                        Debug.Log("[灰盒] 远端规划通过校验：" + remotePlan.plannerNote);
                        return remotePlan;
                    }
                    Debug.LogWarning("[灰盒] 远端规划未通过校验（" + reason + "），回退确定性规划。");
                }
            }
            else
            {
                Debug.Log("[灰盒] 未配置远端 LLM，直接使用确定性灰盒规划（本地兜底）。");
            }

            GrayboxPlan fallback = planner.Plan(request);
            GrayboxPlanner.Sanitize(fallback, request.scene);
            planner.CompleteBindings(fallback, request.scene);
            GrayboxPlanner.Sanitize(fallback, request.scene);

            string fallbackReason;
            if (!GrayboxPlanner.Validate(fallback, request.scene, out fallbackReason))
                Debug.LogWarning("[灰盒] 确定性规划自检未通过：" + fallbackReason + "（仍然返回，避免阻断主流程）");
            return fallback;
        }

        // ─────────────────────────── 远端多轮工具调用 ───────────────────────────

        private async Task<GrayboxPlan> PlanRemoteAsync(GrayboxRequest request, ILlmService service, CancellationToken ct)
        {
            SceneSpec scene = request.scene;
            var plan = new GrayboxPlan
            {
                sceneId = string.IsNullOrEmpty(scene.sceneId) ? "scene_remote" : scene.sceneId,
                width = scene.width > 1f ? scene.width : 60f,
                length = scene.length > 1f ? scene.length : 60f,
                seed = GrayboxPlanner.ResolveSeed(request),
            };

            // 先用确定性解析（吃剧情坐标）生成"地编初稿"：远端只做增删改，
            // 这样超时/只完成一轮也不会丢掉剧情描述里的布局。
            GrayboxPlan draft = planner.Plan(request);
            if (draft != null)
            {
                plan.width = draft.width > 1f ? draft.width : plan.width;
                plan.length = draft.length > 1f ? draft.length : plan.length;
                plan.regions = draft.regions;
                plan.paths = draft.paths;
                plan.spawnPoint = draft.spawnPoint;
                plan.npcPaths = draft.npcPaths;
            }

            List<LlmToolDef> tools = GrayboxToolSchema.CreateToolDefs();
            string systemPrompt = GrayboxToolSchema.BuildSystemPrompt(scene);
            string userPrompt = GrayboxToolSchema.BuildUserPrompt(request);
            if (draft != null)
            {
                userPrompt += "\n\n【地编初稿（由剧情布局描述解析而来，请在此基础上修正，不要推翻重做）】\n"
                              + GrayboxToolSchema.DescribePlan(draft)
                              + "\n请只调用工具修正不合理的分区/道路/槽位，然后调用 finish_graybox。";
            }
            bool finished = false;
            bool spawnSet = false;
            int appliedCalls = 0;

            int rounds = Mathf.Clamp(ToolRounds, 1, MaxToolRounds);
            for (int round = 0; round < rounds; round++)
            {
                ct.ThrowIfCancellationRequested();
                string roundUserPrompt = round == 0
                    ? userPrompt
                    : userPrompt + "\n\n【已完成的编辑】\n" + GrayboxToolSchema.DescribePlan(plan)
                        + "\n请继续调用工具补齐剩余内容；如果已经足够，这一轮请只调用 set_spawn 与 finish_graybox 收尾。";

                LlmToolResponse response = await service.CompleteWithToolsAsync(systemPrompt, roundUserPrompt, tools, 0.2f, ct);
                if (response == null)
                {
                    Debug.LogWarning("[灰盒] 远端工具调用返回 null。");
                    return null;
                }
                if (response.HasError)
                {
                    Debug.LogWarning("[灰盒] 远端工具调用报错: " + response.error);
                    return null;
                }
                if (response.calls == null || response.calls.Count == 0)
                {
                    Debug.LogWarning("[灰盒] 远端没有返回任何工具调用（content=" + Summarize(response.content)
                                     + "），保留地编初稿继续。");
                    break;
                }

                bool progressed = false;
                for (int i = 0; i < response.calls.Count; i++)
                {
                    LlmToolCall call = response.calls[i];
                    if (call == null) continue;
                    bool ok = ApplyToolCall(plan, call, ref spawnSet);
                    if (ok) { progressed = true; appliedCalls++; }
                    if (call.name == GrayboxToolSchema.ToolFinishGraybox) finished = true;
                }

                if (finished) break;
                if (!progressed)
                {
                    Debug.LogWarning("[灰盒] 本轮工具调用全部解析失败，提前结束远端规划。");
                    break;
                }
            }

            if (plan.regions == null || plan.regions.Count == 0)
            {
                Debug.LogWarning("[灰盒] 远端规划没有产出任何功能区域。");
                return null;
            }

            // 远端常常只改分区/道路、漏掉具体槽位：用初稿里的地标槽补足，避免"空场地"
            if (draft != null && draft.slots != null && (plan.slots == null || plan.slots.Count < 6))
            {
                if (plan.slots == null) plan.slots = new List<GrayboxSlot>();
                for (int i = 0; i < draft.slots.Count; i++)
                {
                    GrayboxSlot candidate = draft.slots[i];
                    if (candidate == null) continue;
                    bool tooClose = false;
                    for (int k = 0; k < plan.slots.Count; k++)
                    {
                        GrayboxSlot existing = plan.slots[k];
                        if (existing == null) continue;
                        if (Vector2.Distance(existing.position, candidate.position) < 3f) { tooClose = true; break; }
                    }
                    if (!tooClose) plan.slots.Add(candidate);
                }
                Debug.Log("[灰盒] 远端槽位不足，已用初稿地标槽补到 " + plan.slots.Count + " 个。");
            }

            if (!spawnSet) Debug.Log("[灰盒] 远端未给出出生点，将由本地按场景边界补一个入口出生点。");
            plan.plannerNote = (finished ? "远端 LLM 灰盒工具调用完成" : "远端 LLM 灰盒工具调用（未显式 finish，按已收集内容收尾）")
                               + " · 工具调用 " + appliedCalls + " 次 · 槽位 " + (plan.slots == null ? 0 : plan.slots.Count);
            return plan;
        }

        private static bool ApplyToolCall(GrayboxPlan plan, LlmToolCall call, ref bool spawnSet)
        {
            string name = call.name ?? string.Empty;
            string arguments = call.argumentsJson ?? string.Empty;

            try
            {
                switch (name)
                {
                    case GrayboxToolSchema.ToolDefineRegion:
                        return ApplyDefineRegion(plan, arguments);

                    case GrayboxToolSchema.ToolDefinePath:
                        return ApplyDefinePath(plan, arguments);

                    case GrayboxToolSchema.ToolDefineNpcRoute:
                        return ApplyDefineNpcRoute(plan, arguments);

                    case GrayboxToolSchema.ToolPlaceSlot:
                        return ApplyPlaceSlot(plan, arguments);

                    case GrayboxToolSchema.ToolSetSpawn:
                        {
                            var args = ParseArgs<GrayboxToolSchema.SetSpawnArgs>(arguments);
                            if (args == null) return false;
                            plan.spawnPoint = new Vector2(args.x, args.y);
                            spawnSet = true;
                            return true;
                        }

                    case GrayboxToolSchema.ToolFinishGraybox:
                        {
                            var args = ParseArgs<GrayboxToolSchema.FinishGrayboxArgs>(arguments);
                            if (args != null && !string.IsNullOrEmpty(args.note))
                                plan.plannerNote = args.note.Trim();
                            return true;
                        }

                    default:
                        Debug.LogWarning("[灰盒] 未知工具调用，已忽略: " + name);
                        return false;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[灰盒] 解析工具调用 " + name + " 失败: " + e.Message);
                return false;
            }
        }

        private static bool ApplyDefineRegion(GrayboxPlan plan, string arguments)
        {
            var args = ParseArgs<GrayboxToolSchema.DefineRegionArgs>(arguments);
            if (args == null) return false;
            List<Vector2> polygon = ReadPoints(arguments, args.polygon, "polygon");
            if (polygon.Count < 3) return false;

            string regionId = string.IsNullOrEmpty(args.regionId)
                ? "region_remote_" + plan.regions.Count.ToString(CultureInfo.InvariantCulture)
                : args.regionId;
            plan.regions.Add(new GrayboxRegion
            {
                regionId = regionId,
                kind = string.IsNullOrEmpty(args.kind) ? "residential" : args.kind,
                description = args.description ?? string.Empty,
                style = args.style ?? string.Empty,
                polygon = polygon,
                groundHeight = args.groundHeight,
                areaWeight = args.areaWeight <= 0f ? 1f : args.areaWeight,
            });
            return true;
        }

        private static bool ApplyDefinePath(GrayboxPlan plan, string arguments)
        {
            var args = ParseArgs<GrayboxToolSchema.DefinePathArgs>(arguments);
            if (args == null) return false;
            List<Vector2> nodes = ReadPoints(arguments, args.nodes, "nodes");
            if (nodes.Count < 2) return false;

            string pathId = string.IsNullOrEmpty(args.pathId)
                ? "path_remote_" + plan.paths.Count.ToString(CultureInfo.InvariantCulture)
                : args.pathId;
            plan.paths.Add(new GrayboxPath
            {
                pathId = pathId,
                kind = GrayboxPlanner.NormalizePathKind(args.kind),
                nodes = nodes,
                width = args.width,
            });
            return true;
        }

        private static bool ApplyDefineNpcRoute(GrayboxPlan plan, string arguments)
        {
            var args = ParseArgs<GrayboxToolSchema.DefineNpcRouteArgs>(arguments);
            if (args == null) return false;
            List<Vector2> waypoints = ReadPoints(arguments, args.waypoints, "waypoints");
            if (waypoints.Count < 2) return false;
            if (string.IsNullOrEmpty(args.npcId)) return false;

            bool loop = true;
            if (arguments.IndexOf("\"loop\"", StringComparison.Ordinal) >= 0) loop = args.loop;

            plan.npcPaths.Add(new NpcPath
            {
                npcId = args.npcId,
                waypoints = waypoints,
                loop = loop,
                speed = args.speed,
            });
            return true;
        }

        private static bool ApplyPlaceSlot(GrayboxPlan plan, string arguments)
        {
            var args = ParseArgs<GrayboxToolSchema.PlaceSlotArgs>(arguments);
            if (args == null) return false;
            string semantic = (args.semantic ?? string.Empty).Trim();
            string hint = (args.expectedModelHint ?? string.Empty).Trim();
            if (semantic.Length == 0 && hint.Length == 0) return false;

            string slotId = string.IsNullOrEmpty(args.slotId)
                ? "slot_remote_" + plan.slots.Count.ToString(CultureInfo.InvariantCulture)
                : args.slotId;
            plan.slots.Add(new GrayboxSlot
            {
                slotId = slotId,
                regionId = args.regionId ?? string.Empty,
                semantic = semantic.Length > 0 ? semantic : hint,
                position = new Vector2(args.x, args.y),
                rotation = args.rotation,
                sizeX = args.sizeX <= 0f ? 2f : args.sizeX,
                sizeY = args.sizeY <= 0f ? 2f : args.sizeY,
                kind = GrayboxPlanner.ParseSlotKind(args.kind),
                bindId = args.bindId ?? string.Empty,
                expectedModelHint = hint,
            });
            return true;
        }

        // ─────────────────────────── 解析辅助 ───────────────────────────

        private static T ParseArgs<T>(string json) where T : class
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                return JsonUtility.FromJson<T>(json);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[灰盒] 工具参数不是合法 JSON（" + typeof(T).Name + "）：" + e.Message);
                return null;
            }
        }

        /// <summary>优先用 JsonUtility 反序列化出的 float[]，失败则按字符扫描抽取扁平数字数组。</summary>
        private static List<Vector2> ReadPoints(string json, float[] parsed, string fieldName)
        {
            float[] values = parsed;
            if (values == null || values.Length < 4)
            {
                float[] extracted;
                if (GrayboxToolSchema.TryExtractFlatNumbers(json, fieldName, out extracted)) values = extracted;
            }
            return GrayboxToolSchema.ToPoints(values, 128);
        }

        private static string Summarize(string text)
        {
            if (string.IsNullOrEmpty(text)) return "（空）";
            string single = text.Replace('\n', ' ').Replace('\r', ' ');
            return single.Length <= 120 ? single : single.Substring(0, 120) + "…";
        }
    }
}
