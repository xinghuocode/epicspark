// 场景质量审计：不进入 Play，直接把 剧情 → 灰盒 → 搭建 三段产物 dump 到 Logs/scene-audit.json。
// 用法（Unity 关闭时）：
//   Unity.exe -batchmode -projectPath <proj> -executeMethod EpicSpark.EditorTools.EpicSparkSceneAudit.Run -logFile Logs\scene-audit.log
// 目的：定位"场景像乱拼"到底坏在哪一段（文本描述没结构 / 灰盒没规划 / 搭建选错资产 / 装配丢了骨架）。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EpicSpark.AI.Knowledge;
using EpicSpark.AI.OpenAI;
using EpicSpark.Bootstrap;
using EpicSpark.Build;
using EpicSpark.Core;
using System.IO;
using EpicSpark.Graybox;
using EpicSpark.Loading;
using EpicSpark.Story;
using UnityEditor;
using UnityEngine;

namespace EpicSpark.EditorTools
{
    public static class EpicSparkSceneAudit
    {
        private static Task _task;
        private static readonly StringBuilder Report = new StringBuilder();
        private static string _outputPath;
        private static bool _finished;

        public static void Run()
        {
            _outputPath = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(Application.dataPath) ?? ".", "Logs", "scene-audit.json");
            Debug.Log("[审计] 开始，输出 " + _outputPath);
            _finished = false;
            EditorApplication.update += Tick;
            _task = AuditAsync();
        }

        private static void Tick()
        {
            if (_task == null || !_task.IsCompleted) return;
            if (_finished) return;
            _finished = true;
            EditorApplication.update -= Tick;

            if (_task.IsFaulted)
                Debug.LogError("[审计] 失败: " + (_task.Exception != null ? _task.Exception.ToString() : "unknown"));
            try
            {
                System.IO.File.WriteAllText(_outputPath, Report.ToString(), new UTF8Encoding(false));
                Debug.Log("[审计] 已写出 " + _outputPath);
            }
            catch (Exception e)
            {
                Debug.LogError("[审计] 写文件失败: " + e.Message);
            }
            EditorApplication.Exit(_task.IsFaulted ? 2 : 0);
        }

        private static async Task AuditAsync()
        {
            ApiConfig cfg = ApiConfig.Load();
            Debug.Log("[审计] 配置 " + cfg.Describe());

            OpenAISettings settings = ScriptableObject.CreateInstance<OpenAISettings>();
            settings.baseUrl = cfg.ResolvedBaseUrl;
            settings.apiKey = cfg.apiKey;
            settings.chatModel = cfg.ResolvedChatModel;
            settings.embedModel = cfg.ResolvedEmbedModel;
            settings.timeoutSeconds = cfg.ResolvedTimeoutSeconds;
            settings.maxTokens = cfg.ResolvedMaxTokens;
            settings.maxRetries = 1;
            settings.offline = cfg.offline || !cfg.HasKey;

            var llm = new LlmService(settings);
            SceneAuditBegin(llm, cfg);

            const string eventName = "法国大革命 攻占巴士底狱";
            bool offlineMode = HasArg("-offline");
            string specPath = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(Application.dataPath) ?? ".", "Logs", "scene-spec.json");

            SceneSpec cached = null;
            if (offlineMode && System.IO.File.Exists(specPath))
            {
                try
                {
                    cached = JsonUtility.FromJson<SceneSpec>(System.IO.File.ReadAllText(specPath));
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[审计] 读取缓存场景失败: " + e.Message);
                }
                Debug.Log("[审计] 离线模式：复用 " + specPath);
            }

            // 知识库走本地向量（与游戏内默认一致），拿到真实检索上下文
            KnowledgeSettings knowledgeSettings = ScriptableObject.CreateInstance<KnowledgeSettings>();
            knowledgeSettings.baseUrl = cfg.ResolvedBaseUrl;
            knowledgeSettings.apiKey = cfg.apiKey;
            knowledgeSettings.embedModel = cfg.ResolvedEmbedModel;
            knowledgeSettings.timeoutSeconds = cfg.ResolvedTimeoutSeconds;
            knowledgeSettings.embedProvider = KnowledgeProviderMode.Local;
            knowledgeSettings.rerankProvider = KnowledgeProviderMode.Local;
            var knowledge = new KnowledgeService(knowledgeSettings);
            var kw = System.Diagnostics.Stopwatch.StartNew();
            string knowledgeContext = "";
            long knowledgeMs = 0;
            StoryTurn turn = null;
            long storyMs = 0;
            var story = new StoryAgent(llm) { RemoteTimeoutSeconds = cfg.ResolvedTimeoutSeconds };
            var graybox = new GrayboxAgent(llm) { RemoteTimeoutSeconds = 150, ToolRounds = 2 };
            var build = new BuildAgent();

            // -spec <路径>：直接载入指定场景 JSON（可从调试目录的 04_场景JSON.txt 复现某一轮）
            string specOverride = ArgValue("-spec");
            if (!string.IsNullOrEmpty(specOverride) && File.Exists(specOverride))
            {
                string specText = File.ReadAllText(specOverride);
                var loaded = JsonUtility.FromJson<SceneSpec>(specText);
                if (loaded != null) cached = loaded;
            }

            if (cached != null)
            {
                turn = new StoryTurn { narrative = "(离线：复用缓存场景)" };
                turn.scenes.Add(cached);
                graybox = new GrayboxAgent(null);
                Services.Llm = null; // 离线：别让灰盒再打远端
            }
            else
            {
                try
                {
                    knowledgeContext = await knowledge.GetContextAsync(eventName, CancellationToken.None);
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[审计] 知识库失败: " + e.Message);
                }
                knowledgeMs = kw.ElapsedMilliseconds;

                var storyWatch = System.Diagnostics.Stopwatch.StartNew();
                turn = await story.BeginAsync(eventName, knowledgeContext, CancellationToken.None);
                storyMs = storyWatch.ElapsedMilliseconds;
            }

            if (turn == null || turn.scenes == null || turn.scenes.Count == 0)
            {
                Report.Append("{\"error\":\"story produced no scene\"}");
                return;
            }

            SceneSpec scene = turn.scenes[0];
            if (!offlineMode)
            {
                try
                {
                    System.IO.File.WriteAllText(specPath, JsonUtility.ToJson(scene, true), new UTF8Encoding(false));
                    Debug.Log("[审计] 已缓存场景 spec: " + specPath);
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[审计] 缓存场景失败: " + e.Message);
                }
            }
            Report.Append("{\n");
            Report.Append("  \"event\": ").Append(Quote(eventName)).Append(",\n");
            Report.Append("  \"remoteStory\": ").Append(story.LastTurnFromRemote ? "true" : "false")
                .Append(",\n  \"storyMs\": ").Append(storyMs).Append(",\n");
            Report.Append("  \"knowledgeMs\": ").Append(knowledgeMs).Append(",\n  \"knowledgeLen\": ").Append(knowledgeContext != null ? knowledgeContext.Length : 0).Append(",\n  \"storyError\": ").Append(Quote(story.LastError)).Append(",\n");
            Report.Append("  \"narrative\": ").Append(Quote(turn.narrative)).Append(",\n");
            Report.Append("  \"scene\": {\n");
            Report.Append("    \"id\": ").Append(Quote(scene.sceneId)).Append(",\n");
            Report.Append("    \"name\": ").Append(Quote(scene.sceneName)).Append(",\n");
            Report.Append("    \"size\": [").Append(F(scene.width)).Append(", ").Append(F(scene.length)).Append("],\n");
            Report.Append("    \"layoutDescription\": ").Append(Quote(scene.layoutDescription)).Append(",\n");
            Report.Append("    \"flowDescription\": ").Append(Quote(scene.flowDescription)).Append(",\n");
            Report.Append("    \"buildRequirement\": ").Append(Quote(scene.buildRequirement)).Append(",\n");
            Report.Append("    \"npcs\": ").Append(scene.npcs != null ? scene.npcs.Count : 0).Append(",\n");
            Report.Append("    \"anchors\": ").Append(scene.anchors != null ? scene.anchors.Count : 0).Append(",\n");
            Report.Append("    \"props\": ").Append(scene.props != null ? scene.props.Count : 0).Append("\n  },\n");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            GrayboxPlan plan = await graybox.PlanAsync(new GrayboxRequest { scene = scene, seed = 12345 }, CancellationToken.None);
            long grayboxMs = sw.ElapsedMilliseconds;

            // 统计灰盒产物
            var pathKinds = new Dictionary<string, int>(StringComparer.Ordinal);
            if (plan != null && plan.paths != null)
                foreach (GrayboxPath p in plan.paths)
                {
                    if (p == null) continue;
                    string k = p.kind ?? "?";
                    pathKinds[k] = pathKinds.TryGetValue(k, out int c) ? c + 1 : 1;
                }
            var slotKinds = new Dictionary<string, int>(StringComparer.Ordinal);
            if (plan != null && plan.slots != null)
                foreach (GrayboxSlot s in plan.slots)
                {
                    if (s == null) continue;
                    string k = s.kind.ToString();
                    slotKinds[k] = slotKinds.TryGetValue(k, out int c) ? c + 1 : 1;
                }

            // 布局解析结果：确认"剧情写的坐标"是否被正确抽出来
            StoryLayout layout = StoryLayoutParser.Parse(scene.layoutDescription);
            Report.Append("  \"layout\": {\n");
            Report.Append("    \"usable\": ").Append(layout.Usable ? "true" : "false").Append(",\n");
            Report.Append("    \"note\": ").Append(Quote(layout.note)).Append(",\n");
            Report.Append("    \"items\": [\n");
            for (int li = 0; li < layout.items.Count; li++)
            {
                LayoutItem it = layout.items[li];
                Report.Append("      {\"name\": ").Append(Quote(it.name))
                    .Append(", \"kind\": ").Append(Quote(it.kind))
                    .Append(", \"center\": [").Append(F(it.center.x)).Append(", ").Append(F(it.center.y)).Append("]")
                    .Append(", \"size\": [").Append(F(it.width)).Append(", ").Append(F(it.depth)).Append("]")
                    .Append(", \"count\": ").Append(it.count)
                    .Append(", \"raw\": ").Append(Quote(it.raw))
                    .Append("}").Append(li + 1 < layout.items.Count ? ",\n" : "\n");
            }
            Report.Append("    ],\n    \"zones\": [\n");
            for (int zi = 0; zi < layout.zones.Count; zi++)
            {
                LayoutZone zn = layout.zones[zi];
                Report.Append("      {\"name\": ").Append(Quote(zn.name))
                    .Append(", \"kind\": ").Append(Quote(zn.kind))
                    .Append(", \"x\": [").Append(F(zn.minX)).Append(", ").Append(F(zn.maxX)).Append("]")
                    .Append(", \"z\": [").Append(F(zn.minZ)).Append(", ").Append(F(zn.maxZ)).Append("]")
                    .Append("}").Append(zi + 1 < layout.zones.Count ? ",\n" : "\n");
            }
            Report.Append("    ],\n    \"paths\": [\n");
            for (int pi = 0; pi < layout.paths.Count; pi++)
            {
                LayoutPath lp = layout.paths[pi];
                Report.Append("      {\"name\": ").Append(Quote(lp.name))
                    .Append(", \"width\": ").Append(F(lp.width))
                    .Append(", \"nodes\": [");
                for (int ni = 0; ni < lp.nodes.Count; ni++)
                {
                    if (ni > 0) Report.Append(", ");
                    Report.Append("[").Append(F(lp.nodes[ni].x)).Append(", ").Append(F(lp.nodes[ni].y)).Append("]");
                }
                Report.Append("]}").Append(pi + 1 < layout.paths.Count ? ",\n" : "\n");
            }
            Report.Append("    ]\n  },\n");
            Report.Append("  \"graybox\": {\n");
            Report.Append("    \"ms\": ").Append(grayboxMs).Append(",\n");
            Report.Append("    \"null\": ").Append(plan == null ? "true" : "false").Append(",\n");
            if (plan != null)
            {
                Report.Append("    \"size\": [").Append(F(plan.width)).Append(", ").Append(F(plan.length)).Append("],\n");
                Report.Append("    \"note\": ").Append(Quote(plan.plannerNote)).Append(",\n");
                Report.Append("    \"regions\": ").Append(plan.regions != null ? plan.regions.Count : 0).Append(",\n");
                Report.Append("    \"regionKinds\": [");
                if (plan.regions != null)
                    for (int i = 0; i < plan.regions.Count; i++)
                    {
                        if (i > 0) Report.Append(", ");
                        Report.Append(Quote(plan.regions[i] != null ? plan.regions[i].kind : ""));
                    }
                Report.Append("],\n");
                Report.Append("    \"paths\": ").Append(plan.paths != null ? plan.paths.Count : 0).Append(",\n");
                Report.Append("    \"pathKinds\": {");
                bool first = true;
                foreach (KeyValuePair<string, int> kv in pathKinds)
                {
                    if (!first) Report.Append(", ");
                    first = false;
                    Report.Append(Quote(kv.Key)).Append(": ").Append(kv.Value);
                }
                Report.Append("},\n");
                Report.Append("    \"slots\": ").Append(plan.slots != null ? plan.slots.Count : 0).Append(",\n");
                Report.Append("    \"slotKinds\": {");
                first = true;
                foreach (KeyValuePair<string, int> kv in slotKinds)
                {
                    if (!first) Report.Append(", ");
                    first = false;
                    Report.Append(Quote(kv.Key)).Append(": ").Append(kv.Value);
                }
                Report.Append("},\n");
                Report.Append("    \"npcPaths\": ").Append(plan.npcPaths != null ? plan.npcPaths.Count : 0).Append(",\n");
                Report.Append("    \"slotDetail\": [\n");
                if (plan.slots != null)
                    for (int i = 0; i < plan.slots.Count; i++)
                    {
                        GrayboxSlot s = plan.slots[i];
                        if (s == null) continue;
                        Report.Append("      {\"i\": ").Append(i)
                            .Append(", \"kind\": ").Append(Quote(s.kind.ToString()))
                            .Append(", \"sem\": ").Append(Quote(s.semantic))
                            .Append(", \"pos\": [").Append(F(s.position.x)).Append(", ").Append(F(s.position.y)).Append("]")
                            .Append(", \"rot\": ").Append(F(s.rotation))
                            .Append(", \"size\": [").Append(F(s.sizeX)).Append(", ").Append(F(s.sizeY)).Append("]")
                            .Append(", \"region\": ").Append(Quote(s.regionId))
                            .Append(", \"bind\": ").Append(Quote(s.bindId))
                            .Append(", \"hint\": ").Append(Quote(s.expectedModelHint))
                            .Append("}").Append(i + 1 < plan.slots.Count ? ",\n" : "\n");
                    }
                Report.Append("    ]\n");
            }
            Report.Append("  },\n");

            // 搭建阶段
            var catalog = new ModelCatalog();
            build = new BuildAgent(catalog);
            sw.Restart();
            BuildResult result = await build.BuildAsync(
                new BuildRequest { scene = scene, plan = plan, sliderValue = 0.35f }, CancellationToken.None);
            long buildMs = sw.ElapsedMilliseconds;

            var nameById = new Dictionary<string, string>(StringComparer.Ordinal);
            var categoryById = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                await catalog.LoadAsync(CancellationToken.None);
                foreach (ModelEntry entry in catalog.Snapshot())
                {
                    if (entry == null) continue;
                    nameById[entry.id] = entry.name;
                    categoryById[entry.id] = entry.category;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[审计] 模型库读取失败: " + e.Message);
            }

            // 可视化验证：装配 + 调试截图（-render 时执行），用来确认"地编"和调试录制是否真的产出图像
            SceneInstance auditedInstance = null;
            if (HasArg("-render"))
            {
                try
                {
                    string renderDir = System.IO.Path.Combine(Directory.GetCurrentDirectory(), "Logs", "debug-render");
                    Directory.CreateDirectory(renderDir);
                    EpicSpark.Debugging.SceneDebugRecorder.CurrentDir = renderDir;

                    var root = new GameObject("AuditSceneRoot");
                    SceneInstance instance = await SceneAssembler.AssembleAsync(
                        scene, plan, result, root.transform, CancellationToken.None);
                    auditedInstance = instance;

                    // 补光：批处理下没有场景灯，否则截图全黑
                    var lightGo = new GameObject("AuditLight");
                    var light = lightGo.AddComponent<Light>();
                    light.type = LightType.Directional;
                    light.intensity = 1.15f;
                    light.color = new Color(1f, 0.96f, 0.88f);
                    lightGo.transform.rotation = Quaternion.Euler(48f, -35f, 0f);
                    RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
                    RenderSettings.ambientLight = new Color(0.32f, 0.34f, 0.38f);

                    float span = Mathf.Max(instance.width, instance.length) * 0.62f;

                    // 07：只留灰盒层
                    SetActiveSafe(instance.groundRoot, false);
                    SetActiveSafe(instance.modelRoot, false);
                    SetActiveSafe(instance.wireRoot, false);
                    SetActiveSafe(instance.grayboxRoot, true);
                    bool grayOk = EpicSpark.Debugging.SceneDebugRecorder.CaptureTopDown(
                        renderDir, "07_灰盒规划_顶视.png", instance.grayboxRoot, Vector3.zero, span);

                    // 09/10：点亮贴图模型，撤掉灰盒与棱线
                    SetActiveSafe(instance.grayboxRoot, false);
                    SetActiveSafe(instance.groundRoot, true);
                    SetActiveSafe(instance.modelRoot, true);
                    SceneLoadingSequence.RevealImmediate(instance);
                    bool topOk = EpicSpark.Debugging.SceneDebugRecorder.CaptureTopDown(
                        renderDir, "09_拼装_顶视.png", instance.root, Vector3.zero, span);
                    var viewCam = new GameObject("AuditViewCam").AddComponent<Camera>();
                    viewCam.transform.position = instance.spawnPoint + new Vector3(0f, 15f, -28f);
                    viewCam.transform.rotation = Quaternion.Euler(20f, 0f, 0f);
                    bool viewOk = EpicSpark.Debugging.SceneDebugRecorder.CaptureView(
                        renderDir, "10_拼装_玩家视角.png", viewCam, 62f);
                    Debug.Log("[审计] 截图: 灰盒=" + grayOk + " 顶视=" + topOk + " 视角=" + viewOk
                              + " 目录=" + renderDir + " 模型=" + instance.models.Count);
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[审计] 装配/截图失败: " + e);
                }
            }

            Report.Append("  \"build\": {\n");
            Report.Append("    \"ms\": ").Append(buildMs).Append(",\n");
            Report.Append("    \"placed\": ").Append(result != null && result.placed != null ? result.placed.Count : 0).Append(",\n");
            Report.Append("    \"generated\": ").Append(result != null ? result.generationCount : 0).Append(",\n");
            Report.Append("    \"warnings\": ").Append(result != null && result.warnings != null ? result.warnings.Count : 0).Append(",\n");
            Report.Append("    \"warningList\": [");
            if (result != null && result.warnings != null)
                for (int i = 0; i < result.warnings.Count; i++)
                {
                    if (i > 0) Report.Append(", ");
                    Report.Append(Quote(result.warnings[i]));
                }
            Report.Append("],\n");

            // 质量指标
            var placed = result != null && result.placed != null ? result.placed : new List<PlacedAsset>();
            int reuse = 0, gen = 0;
            var byModel = new Dictionary<string, int>(StringComparer.Ordinal);
            float minSize = float.MaxValue, maxSize = 0f, sumSize = 0f;
            int scaled = 0;
            var grid = new int[5, 5];
            foreach (PlacedAsset p in placed)
            {
                if (p == null) continue;
                if (p.kind == SlotKind.Npc || p.kind == SlotKind.Anchor) continue;
                if (p.generated) gen++; else if (!string.IsNullOrEmpty(p.modelId)) reuse++;
                if (!string.IsNullOrEmpty(p.modelId))
                    byModel[p.modelId] = byModel.TryGetValue(p.modelId, out int c) ? c + 1 : 1;
                float s = Mathf.Max(p.sizeX, p.sizeY);
                minSize = Mathf.Min(minSize, s);
                maxSize = Mathf.Max(maxSize, s);
                sumSize += s;
                scaled++;
                int gx = Mathf.Clamp((int)((p.position.x / Mathf.Max(1f, plan.width) + 0.5f) * 5f), 0, 4);
                int gz = Mathf.Clamp((int)((p.position.z / Mathf.Max(1f, plan.length) + 0.5f) * 5f), 0, 4);
                grid[gx, gz]++;
            }
            Report.Append("    \"reuseCount\": ").Append(reuse).Append(",\n");
            Report.Append("    \"generatedCount\": ").Append(gen).Append(",\n");
            Report.Append("    \"distinctModels\": ").Append(byModel.Count).Append(",\n");
            Report.Append("    \"size\": {\"min\": ").Append(F(minSize == float.MaxValue ? 0f : minSize))
                .Append(", \"max\": ").Append(F(maxSize))
                .Append(", \"avg\": ").Append(F(scaled > 0 ? sumSize / scaled : 0f)).Append("},\n");

            // 空间约束指标：不允许穿模 / NPC 与锚点不落模型内 / 每个模型都有空气墙。
            // overlapPairs 用与求解器一致的平面 OBB（SAT）判定；-render 时再统计装配后的真实包围盒与空气墙数量。
            int overlaps = ScenePlacementSolver.CountModelOverlaps(placed, ScenePlacementSolver.ModelGap);
            int npcInside = 0, anchorInside = 0;
            int unitViolations = ScenePlacementSolver.CountUnitModelViolations(placed, plan, out npcInside, out anchorInside);
            int exactModelOverlaps = 0;
            int modelWallOverlaps = 0;
            int airWalls = 0;
            if (auditedInstance != null)
            {
                exactModelOverlaps = ScenePlacementSolver.CountFootprintOverlaps(
                    auditedInstance.modelFootprints, null, ScenePlacementSolver.ModelGap);
                modelWallOverlaps = ScenePlacementSolver.CountFootprintOverlaps(
                    auditedInstance.modelFootprints, auditedInstance.wallFootprints, ScenePlacementSolver.ModelGap);
                airWalls = auditedInstance.airWallCount;
            }
            Report.Append("    \"overlapPairs\": ").Append(overlaps).Append(",\n");
            Report.Append("    \"exactModelOverlaps\": ").Append(exactModelOverlaps).Append(",\n");
            Report.Append("    \"modelWallOverlaps\": ").Append(modelWallOverlaps).Append(",\n");
            Report.Append("    \"actorInsideModel\": ").Append(unitViolations).Append(",\n");
            Report.Append("    \"npcInsideModel\": ").Append(npcInside).Append(",\n");
            Report.Append("    \"anchorInsideModel\": ").Append(anchorInside).Append(",\n");
            Report.Append("    \"airWalls\": ").Append(airWalls).Append(",\n");
            Report.Append("    \"modelsRendered\": ").Append(auditedInstance != null ? auditedInstance.models.Count : 0).Append(",\n");
            Report.Append("    \"grid5x5\": [");
            for (int gz = 0; gz < 5; gz++)
            {
                if (gz > 0) Report.Append(", ");
                Report.Append("[");
                for (int gx = 0; gx < 5; gx++)
                {
                    if (gx > 0) Report.Append(", ");
                    Report.Append(grid[gx, gz]);
                }
                Report.Append("]");
            }
            Report.Append("],\n");

            Report.Append("    \"placedDetail\": [\n");
            for (int i = 0; i < placed.Count; i++)
            {
                PlacedAsset p = placed[i];
                if (p == null) continue;
                string modelName = nameById.TryGetValue(p.modelId, out string mn) ? mn : "";
                string category = categoryById.TryGetValue(p.modelId, out string ct2) ? ct2 : "";
                Report.Append("      {\"slot\": ").Append(Quote(p.slotId))
                    .Append(", \"kind\": ").Append(Quote(p.kind.ToString()))
                    .Append(", \"model\": ").Append(Quote(p.modelId))
                    .Append(", \"assetName\": ").Append(Quote(modelName))
                    .Append(", \"category\": ").Append(Quote(category))
                    .Append(", \"score\": ").Append(F(p.matchScore))
                    .Append(", \"generated\": ").Append(p.generated ? "true" : "false")
                    .Append(", \"pos\": [").Append(F(p.position.x)).Append(", ").Append(F(p.position.z)).Append("]")
                    .Append(", \"rotY\": ").Append(F(p.rotationY))
                    .Append(", \"size\": [").Append(F(p.sizeX)).Append(", ").Append(F(p.sizeY)).Append("]")
                    .Append("}").Append(i + 1 < placed.Count ? ",\n" : "\n");
            }
            Report.Append("    ]\n");
            Report.Append("  }\n}\n");

            Debug.Log("[审计] 完成 剧情=" + storyMs + "ms 灰盒=" + grayboxMs + "ms 搭建=" + buildMs + "ms 落位=" + placed.Count
                      + " 重叠=" + overlaps + " NPC入模=" + npcInside + " 锚点入模=" + anchorInside
                      + " 空气墙=" + airWalls + " 模型种类=" + byModel.Count);
        }

        private static void SceneAuditBegin(LlmService llm, ApiConfig cfg)
        {
            Services.Llm = llm;
            Services.Catalog = null;
            Services.Save = new GameSaveData();
        }

        private static void SetActiveSafe(GameObject go, bool active)
        {
            if (go != null) go.SetActive(active);
        }

        private static string ArgValue(string flag)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return "";
        }

        private static bool HasArg(string flag)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static string F(float v)
        {
            return v.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static string Quote(string s)
        {
            if (s == null) return "\"\"";
            var sb = new StringBuilder(s.Length + 8);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append(' ');
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
