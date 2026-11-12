// 灰盒 Agent：正式路径把 SceneSpec 的详细需求显式传给 GrayboxToolWorkspace，
// 通过受限工具生成独立、可校验的唯一空间状态；剧情对象与语义输入均不被就地修改。
// 旧远端低阶坐标编辑与 GrayboxPlanner 仅保留给 EPICSPARK_SCENEGEN=0 的开发对照。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using EpicSpark.Core;
using EpicSpark.SceneGen;
using EpicSpark.Story;
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
        public int RemoteTimeoutSeconds = 0;   // 0 = 不限时（推理模型单轮可能 1–3 分钟）

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

        /// <summary>
        /// 远端调用预算。RemoteTimeoutSeconds <= 0 表示不限时：推理型模型一轮 60–120 秒是正常的，
        /// 到点就砍只会白等。此时只受取消令牌（切场景/退出）与 HTTP 超时（ApiConfig）约束。
        /// </summary>
        private async Task<GrayboxPlan> WithRemoteBudgetAsync(Task<GrayboxPlan> task, string label)
        {
            if (task == null) return null;
            if (RemoteTimeoutSeconds <= 0)
            {
                Debug.Log("[灰盒] " + label + " 不限时（推理型模型可能跑 1–3 分钟），等待结果…");
                return await task;
            }

            int budget = Mathf.Max(5, RemoteTimeoutSeconds);
            Task winner = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(budget)));
            if (winner != task)
            {
                Debug.LogWarning("[灰盒] " + label + " 超过 " + budget + " 秒未返回，改用确定性规划继续。");
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

            // 当前正式链路：剧情需求 → 受限灰盒工具 → 独立且已校验的 GrayboxScene。
            // 失败必须显式终止，禁止再退回会随意补点/重排的旧的单体求解器或网格规划。
            if (EpicSparkFeatureFlags.UseSceneGenSolver)
            {
                try
                {
                    LayoutSpec semantic = StoryLayoutSpecBuilder.Build(request.scene);
                    GrayboxPlan toolPlan = SceneGenPipeline.Plan(
                        request.scene, semantic, GrayboxPlanner.ResolveSeed(request));
                    if (toolPlan != null) return toolPlan;
                    Debug.LogWarning("[灰盒] 工具工作区未能生成合法场景，已阻断本次搭建。");
                    return null;
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[灰盒] 工具工作区异常，已阻断本次搭建: " + e);
                    return null;
                }
            }

            GrayboxPlan specPlan = null;
            LayoutSpec spec = null;

            // 以下为明确设置 EPICSPARK_SCENEGEN=0 时的历史对照链路。
            if (EpicSparkFeatureFlags.UseLayoutSpecExecutor)
            {
                try
                {
                    spec = StoryLayoutSpecBuilder.Build(request.scene);
                    if (spec != null && spec.Usable || EpicSparkFeatureFlags.UseSceneGenSolver)
                    {
                        string specNote;
                        specPlan = GrayboxSpecExecutor.Build(
                            request.scene, spec, GrayboxPlanner.ResolveSeed(request), out specNote);
                        if (specPlan != null)
                        {
                            GrayboxPlanner.Sanitize(specPlan, request.scene);
                            GrayboxPlanner.AssignStreamingSegments(specPlan);
                            string specReason;
                            if (!GrayboxPlanner.Validate(specPlan, request.scene, out specReason))
                            {
                                Debug.LogWarning("[灰盒] LayoutSpec 执行器自检未通过（" + specReason + "），回退旧规划。");
                                specPlan = null;
                            }
                            else if (EpicSparkFeatureFlags.UseSceneGenSolver)
                            {
                                // 求解器计划的坐标/尺寸由我们自己保证，再跑旧版"规格合规"只会刷屏误报。
                                string kpi = string.IsNullOrEmpty(specPlan.sceneGenReport) ? "（无报告）" : specPlan.sceneGenReport;
                                Debug.Log("[灰盒] " + specNote + " · " + kpi);
                            }
                            else
                            {
                                List<string> specErrors;
                                List<string> specWarnings;
                                bool specCompliant = GrayboxSpecCompliance.Validate(
                                    specPlan, spec, out specErrors, out specWarnings, false);
                                for (int i = 0; i < specWarnings.Count; i++)
                                    Debug.LogWarning("[灰盒合规] 执行器提醒: " + specWarnings[i]);
                                for (int i = 0; i < specErrors.Count && i < 8; i++)
                                    Debug.LogWarning("[灰盒合规] 执行器错误: " + specErrors[i]);
                                Debug.Log("[灰盒] " + specNote + (specCompliant ? " · 合规通过" : " · 合规失败 " + specErrors.Count + " 项"));
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[灰盒] LayoutSpec 构建/执行异常，回退旧规划: " + e.Message);
                    spec = null;
                    specPlan = null;
                }
            }

            if (service != null && service.HasRemote)
            {
                GrayboxPlan remotePlan = null;
                try
                {
                    remotePlan = await WithRemoteBudgetAsync(
                        PlanRemoteAsync(request, service, ct, specPlan, spec), "灰盒规划");
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
                    if (spec == null)
                    {
                        // 旧模式：保持原有绑定补齐逻辑
                        planner.CompleteBindings(remotePlan, request.scene);
                        GrayboxPlanner.Sanitize(remotePlan, request.scene);
                    }
                    GrayboxPlanner.AssignStreamingSegments(remotePlan);
                    WarnIfSparse(remotePlan, request.scene);

                    string reason;
                    bool geometryOk = GrayboxPlanner.Validate(remotePlan, request.scene, out reason);
                    if (geometryOk && spec != null && spec.Usable)
                    {
                        // 远端可以增删改，但规格槽位必须被恢复；不合规就回退到规格执行器。
                        List<string> errors;
                        List<string> warnings;
                        GrayboxSpecCompliance.MergeRequired(remotePlan, spec);
                        bool specOk = GrayboxSpecCompliance.Validate(
                            remotePlan, spec, out errors, out warnings, EpicSparkFeatureFlags.StrictSpecGeometry);
                        // 只差 finish_graybox 时不必回退：seed 槽位已被 MergeRequired 补齐，
                        // 剧情内容完整；把「没走完协议」降级为提醒，保留远端的微调结果。
                        if (!specOk && OnlyMissingFinish(errors))
                        {
                            specOk = true;
                            remotePlan.specCompliance = "pass · 槽位完整（远端未调用 finish_graybox）";
                            warnings.Add("远端未调用 finish_graybox，但 LayoutSpec 槽位完整，按已收集结果收尾。");
                        }
                        for (int i = 0; i < warnings.Count; i++)
                            Debug.LogWarning("[灰盒合规] " + warnings[i]);
                        if (specOk)
                        {
                            Debug.Log("[灰盒] 远端规划按 LayoutSpec 校验通过：" + remotePlan.specCompliance);
                            return remotePlan;
                        }
                        for (int i = 0; i < errors.Count && i < 8; i++)
                            Debug.LogWarning("[灰盒合规] " + errors[i]);
                        if (EpicSparkFeatureFlags.StrictSpecCompliance && specPlan != null)
                        {
                            Debug.LogWarning("[灰盒] 远端规划偏离剧情规格（" + errors.Count + " 项），回退 LayoutSpec 执行器。");
                            return specPlan;
                        }
                    }
                    else if (geometryOk && spec == null)
                    {
                        Debug.Log("[灰盒] 远端规划通过校验：" + remotePlan.plannerNote);
                        return remotePlan;
                    }
                    else if (geometryOk && spec != null)
                    {
                        Debug.LogWarning("[灰盒合规] 规格存在但远端计划无法校验，回退规格执行器。");
                        if (specPlan != null) return specPlan;
                    }
                    Debug.LogWarning("[灰盒] 远端规划未通过校验（" + reason + "），回退确定性规划。");
                }
            }
            else
            {
                Debug.Log("[灰盒] 未配置远端 LLM，直接使用确定性灰盒规划（本地兜底）。");
            }

            if (specPlan != null)
            {
                Debug.Log("[灰盒] 使用 LayoutSpec 执行器结果（远端不可用/不合规）。");
                return specPlan;
            }

            GrayboxPlan fallback = planner.Plan(request);
            GrayboxPlanner.Sanitize(fallback, request.scene);
            planner.CompleteBindings(fallback, request.scene);
            GrayboxPlanner.Sanitize(fallback, request.scene);
            GrayboxPlanner.AssignStreamingSegments(fallback);

            string fallbackReason;
            if (!GrayboxPlanner.Validate(fallback, request.scene, out fallbackReason))
                Debug.LogWarning("[灰盒] 确定性规划自检未通过：" + fallbackReason + "（仍然返回，避免阻断主流程）");
            return fallback;
        }

        // ─────────────────────────── 远端多轮工具调用 ───────────────────────────

        private async Task<GrayboxPlan> PlanRemoteAsync(
            GrayboxRequest request, ILlmService service, CancellationToken ct,
            GrayboxPlan seedPlan = null, LayoutSpec spec = null)
        {
            SceneSpec scene = request.scene;
            var plan = new GrayboxPlan
            {
                sceneId = string.IsNullOrEmpty(scene.sceneId) ? "scene_remote" : scene.sceneId,
                width = scene.width > 1f ? scene.width : 60f,
                length = scene.length > 1f ? scene.length : 60f,
                seed = GrayboxPlanner.ResolveSeed(request),
            };

            GrayboxPlan draft = null;
            if (seedPlan != null)
            {
                // 执行模式：seed 是 LayoutSpec 执行器生成的规格槽位，必须深拷贝，
                // 否则远端修改会污染回退用的 specPlan。
                CopyPlanSeed(plan, seedPlan);
                plan.layoutSpec = spec;
                plan.specDriven = true;
            }
            else
            {
                // 旧模式：先用确定性解析（吃剧情坐标）生成"地编初稿"。
                draft = planner.Plan(request);
                if (draft != null)
                {
                    plan.width = draft.width > 1f ? draft.width : plan.width;
                    plan.length = draft.length > 1f ? draft.length : plan.length;
                    plan.regions = draft.regions;
                    plan.paths = draft.paths;
                    plan.spawnPoint = draft.spawnPoint;
                    plan.npcPaths = draft.npcPaths;
                }
            }

            List<LlmToolDef> tools = GrayboxToolSchema.CreateToolDefs();
            string systemPrompt = GrayboxToolSchema.BuildSystemPrompt(scene, spec);
            string userPrompt = GrayboxToolSchema.BuildUserPrompt(request, spec);
            GrayboxPlan promptPlan = seedPlan != null ? seedPlan : draft;
            if (promptPlan != null)
            {
                userPrompt += seedPlan != null
                    ? "\n\n【LayoutSpec seed 槽位（硬约束，必须逐项保留；只能微调，不能重规划）】\n"
                      + GrayboxToolSchema.DescribePlan(promptPlan)
                      + "\n请保留每个 seed 槽位的 slotId/specItemId/rowId/rowCount；只在必要时做不超过 1.5 米的避让，"
                      + "然后调用 finish_graybox。"
                    : "\n\n【地编初稿（由剧情布局描述解析而来，请在此基础上修正，不要推翻重做）】\n"
                      + GrayboxToolSchema.DescribePlan(promptPlan)
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

            // 旧模式：远端常常只改分区/道路、漏掉具体槽位，用初稿地标槽补足。
            // 执行模式不走 3 米距离补槽：规格槽位由 GrayboxSpecCompliance.MergeRequired 精确恢复。
            if (spec == null && draft != null && draft.slots != null)
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
                Debug.Log("[灰盒] 已把初稿地标槽并入远端规划，槽位 " + plan.slots.Count + " 个。");
            }

            plan.finished = finished;
            if (!spawnSet) Debug.Log("[灰盒] 远端未给出出生点，将由本地按场景边界补一个入口出生点。");
            plan.plannerNote = (finished ? "远端 LLM 灰盒工具调用完成" : "远端 LLM 灰盒工具调用（未显式 finish，按已收集内容收尾）")
                               + (spec != null ? " · 执行模式" : "")
                               + " · 工具调用 " + appliedCalls + " 次 · 槽位 " + (plan.slots == null ? 0 : plan.slots.Count);
            return plan;
        }

        /// <summary>把 LayoutSpec 执行器的计划深拷贝给远端编辑（保持回退计划不被污染）。</summary>
        private static void CopyPlanSeed(GrayboxPlan target, GrayboxPlan seed)
        {
            if (target == null || seed == null) return;
            target.width = seed.width;
            target.length = seed.length;
            target.seed = seed.seed;
            target.spawnPoint = seed.spawnPoint;
            target.layoutShift = seed.layoutShift;
            target.specDriven = seed.specDriven;
            target.layoutSpec = seed.layoutSpec;
            target.regions = new List<GrayboxRegion>();
            if (seed.regions != null)
                for (int i = 0; i < seed.regions.Count; i++) target.regions.Add(CloneRegion(seed.regions[i]));
            target.paths = new List<GrayboxPath>();
            if (seed.paths != null)
                for (int i = 0; i < seed.paths.Count; i++) target.paths.Add(ClonePath(seed.paths[i]));
            target.slots = new List<GrayboxSlot>();
            if (seed.slots != null)
                for (int i = 0; i < seed.slots.Count; i++) target.slots.Add(CloneSlot(seed.slots[i]));
            target.npcPaths = new List<NpcPath>();
            if (seed.npcPaths != null)
                for (int i = 0; i < seed.npcPaths.Count; i++) target.npcPaths.Add(CloneNpcPath(seed.npcPaths[i]));
        }

        private static GrayboxSlot CloneSlot(GrayboxSlot s)
        {
            if (s == null) return null;
            return new GrayboxSlot
            {
                slotId = s.slotId, regionId = s.regionId, semantic = s.semantic,
                position = s.position, rotation = s.rotation, sizeX = s.sizeX, sizeY = s.sizeY,
                height = s.height,
                kind = s.kind, bindId = s.bindId, expectedModelHint = s.expectedModelHint,
                segment = s.segment, specItemId = s.specItemId, rowId = s.rowId,
                rowIndex = s.rowIndex, rowCount = s.rowCount, specRequired = s.specRequired,
            };
        }

        private static GrayboxRegion CloneRegion(GrayboxRegion r)
        {
            if (r == null) return null;
            var copy = new GrayboxRegion
            {
                regionId = r.regionId, kind = r.kind, description = r.description, style = r.style,
                groundHeight = r.groundHeight, areaWeight = r.areaWeight,
            };
            if (r.polygon != null) copy.polygon = new List<Vector2>(r.polygon);
            return copy;
        }

        private static GrayboxPath ClonePath(GrayboxPath p)
        {
            if (p == null) return null;
            var copy = new GrayboxPath { pathId = p.pathId, kind = p.kind, width = p.width };
            if (p.nodes != null) copy.nodes = new List<Vector2>(p.nodes);
            return copy;
        }

        private static NpcPath CloneNpcPath(NpcPath p)
        {
            if (p == null) return null;
            var copy = new NpcPath { npcId = p.npcId, loop = p.loop, speed = p.speed };
            if (p.waypoints != null) copy.waypoints = new List<Vector2>(p.waypoints);
            return copy;
        }

        private static bool OnlyMissingFinish(List<string> errors)
        {
            if (errors == null || errors.Count == 0) return false;
            for (int i = 0; i < errors.Count; i++)
            {
                if (string.IsNullOrEmpty(errors[i])) continue;
                if (errors[i].IndexOf("finish_graybox", StringComparison.Ordinal) < 0
                    && errors[i].IndexOf("计划不完整", StringComparison.Ordinal) < 0)
                    return false;
            }
            return true;
        }

        /// <summary>密度闸门：120×80 的场地只放 8 栋建筑一定稀疏，记录到日志供排查。</summary>
        private static void WarnIfSparse(GrayboxPlan plan, SceneSpec scene)
        {
            if (plan == null || plan.slots == null || scene == null) return;
            int buildings = 0;
            for (int i = 0; i < plan.slots.Count; i++)
            {
                GrayboxSlot slot = plan.slots[i];
                if (slot != null && slot.kind == SlotKind.Building) buildings++;
            }
            float area = Mathf.Max(1f, scene.width * scene.length);
            int target = Mathf.Max(6, Mathf.RoundToInt(area / 350f));
            if (buildings < target)
                Debug.LogWarning("[灰盒] 建筑槽偏少：" + buildings + " 个（面积 " + area.ToString("F0")
                                 + " 平方米，建议 ≥ " + target + " 个），场景可能显得稀疏。");
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

            // 执行模式下远端可能用同一个 slotId 调整 seed 槽位：必须 upsert，
            // 不能追加一个重复槽位（否则数量校验/重叠校验都会失败）。
            GrayboxSlot existing = null;
            if (plan.slots != null)
            {
                for (int i = 0; i < plan.slots.Count; i++)
                {
                    if (plan.slots[i] != null && plan.slots[i].slotId == slotId) { existing = plan.slots[i]; break; }
                }
            }
            if (existing == null)
            {
                existing = new GrayboxSlot { slotId = slotId };
                plan.slots.Add(existing);
            }
            existing.regionId = args.regionId ?? string.Empty;
            existing.semantic = semantic.Length > 0 ? semantic : hint;
            existing.position = new Vector2(args.x, args.y);
            existing.rotation = args.rotation;
            existing.sizeX = args.sizeX <= 0f ? 2f : args.sizeX;
            existing.sizeY = args.sizeY <= 0f ? 2f : args.sizeY;
            existing.kind = GrayboxPlanner.ParseSlotKind(args.kind);
            existing.bindId = args.bindId ?? string.Empty;
            existing.expectedModelHint = hint;
            // 保留 seed 的规格身份；远端显式给出的非空值优先用于新增槽。
            if (!string.IsNullOrEmpty(args.specItemId)) existing.specItemId = args.specItemId;
            if (!string.IsNullOrEmpty(args.rowId)) existing.rowId = args.rowId;
            if (args.rowCount > 0) existing.rowCount = args.rowCount;
            existing.rowIndex = args.rowIndex;
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
