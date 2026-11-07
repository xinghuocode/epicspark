using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EpicSpark.AI.OpenAI;
using UnityEngine;

namespace EpicSpark.AI.SceneAgentTest
{
    /// <summary>
    /// 场景Agent测试运行器 (ARCHITECT.md §4.2 的测试床)。
    ///
    /// 用法: 在 Inspector 中填入/修改
    ///   1. systemPrompt      —— 场景Agent的系统提示词(测试不同的提示词策略)
    ///   2. generateSceneJson —— 剧情Agent generate_scene 工具的参数 (JSON, §4.1)
    /// 然后点 Play, 右键组件选 "运行场景Agent测试" 或勾选 autoRunOnStart。
    /// 场景Agent通过原生 Function Calling 逐步调用工具搭建场景, 每一步的结果都会打印到
    /// Console 并累计到 Transcript 中(右键 "输出完整场景记录" 查看)。
    /// </summary>
    public class SceneAgentRunner : MonoBehaviour
    {
        [Header("OpenAI 兼容端点配置")]
        public OpenAISettings settings;

        [Header("场景Agent系统提示词 (留空使用内置默认)")]
        [TextArea(8, 40)]
        public string systemPrompt = "";

        [Header("剧情Agent generate_scene 工具参数 (JSON)")]
        [TextArea(10, 40)]
        public string generateSceneJson = "";

        [Header("运行控制")]
        public bool autoRunOnStart = false;

        /// <summary>完整运行记录(逐步场景输出)。</summary>
        public string Transcript => transcript?.ToString() ?? "";

        /// <summary>是否正在运行。</summary>
        public bool IsRunning => running;

        /// <summary>是否已有生成好的场景可进入体验。</summary>
        public bool HasScene => world != null;

        /// <summary>是否正在场景体验中。</summary>
        public bool IsExperiencing => experiencePlayer != null;

        /// <summary>是否正在测试模型生成管线。</summary>
        public bool IsTestingPipeline => testingPipeline;

        /// <summary>
        /// 单独测试 文生图→去背景→图生3D 完整管线(不跑场景Agent)。
        /// 成功后 GLB 保存到 persistentDataPath 并在场景中显示预览。
        /// </summary>
        public async void TestModelPipeline(string prompt)
        {
            if (testingPipeline) { Log("[管线测试] 正在进行中..."); return; }
            if (settings == null) { LogError("[管线测试] 未配置 OpenAISettings"); return; }
            if (string.IsNullOrWhiteSpace(prompt)) { LogError("[管线测试] 提示词为空"); return; }

            testingPipeline = true;
            AppendTranscript("===== 模型生成管线测试 =====\n提示词: " + prompt + "\n");
            Log("[管线测试] 开始(约需1-5分钟): " + prompt);
            var testClient = new OpenAIClient(settings);
            try
            {
                var pipeline = new ModelGenerationPipeline(testClient);
                byte[] glb = await pipeline.GenerateGlbAsync(prompt, s => Log("[管线测试] " + s), CancellationToken.None);
                string dir = Path.Combine(Application.persistentDataPath, "PipelineTest");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "test_model.glb");
                File.WriteAllBytes(path, glb);

                string okMsg = $"管线测试成功! GLB {glb.Length / 1024} KB → {path}";
                Log("[管线测试] " + okMsg);
                AppendTranscript(okMsg + "\n");

                // 场景内预览: 缩放到约8m, 放在起点/场景中心上方
                var gltf = await ModelGltfCache.LoadAsync(path);
                if (gltf != null)
                {
                    var holder = new GameObject("PipelineTestModel");
                    if (await gltf.InstantiateMainSceneAsync(holder.transform))
                    {
                        var renderers = holder.GetComponentsInChildren<Renderer>();
                        if (renderers.Length > 0)
                        {
                            var b = renderers[0].bounds;
                            foreach (var r in renderers) b.Encapsulate(r.bounds);
                            float s = 8f / Mathf.Max(b.size.x, b.size.y, b.size.z, 0.01f);
                            holder.transform.localScale = Vector3.one * s;
                        }
                        float px = world != null && world.HasStartPoint ? (float)world.StartX : 0f;
                        float py = world != null && world.HasStartPoint ? (float)world.StartY : 0f;
                        float gy = world != null ? (float)world.TerrainHeight(px + 6, py + 6) : 0f;
                        holder.transform.position = new Vector3(px + 6, gy, py + 6);
                        Log("[管线测试] 已在场景中生成预览 (PipelineTestModel)");
                    }
                }
            }
            catch (Exception e)
            {
                LogError("[管线测试] 失败: " + e.Message);
                AppendTranscript("管线测试失败: " + e.Message + "\n");
            }
            finally
            {
                testClient.Dispose();
                testingPipeline = false;
            }
        }

        /// <summary>当前执行到的轮数(未开始为 0)。</summary>
        public int CurrentRound { get; private set; }

        /// <summary>清空运行记录。</summary>
        public void ClearTranscript() => transcript?.Clear();

        StringBuilder transcript;
        OpenAIClient client;
        CancellationTokenSource cts;

        // ================== 内置默认提示词 ==================

        public const string DefaultSystemPrompt =
            "你是 EpicSpark 游戏中的场景Agent。你会收到剧情Agent通过 generate_scene 工具发来的场景描述、场景要求、" +
            "传送锚点列表和场景尺寸(宽 width / 长 length)。你的任务是通过依次调用工具, 在平面坐标系(x, y; x 对应东西, y 对应南北," +
            "原点为场景一角, 范围均为 0 到场景尺寸)中搭建出场景。高度由系统自动计算, 不需要你指定。\n" +
            "\n" +
            "工作规则:\n" +
            "- 第一个工具调用必须是 init_terrain。\n" +
            "- 所有模型先从缓存模型库中选择: 先用 search_model 搜索, 返回结果中有 id、名称、提示词; " +
            "选择语义最接近需求的模型, 用其 id 调用 place_model。若库中没有语义接近的模型, 才调用 generate_model 生成" +
            "(耗时可达数分钟, 仅在确实需要时使用), 返回的新模型 id 可立即用于 place_model。\n" +
            "- 布局要符合场景描述与要求, 坐标必须在场景尺寸范围内, 模型之间不要重叠。\n" +
            "- 每个传送锚点都必须用 set_auchor_point 绑定一个符合其描述要求的已放置模型实例。\n" +
            "- 每次回复只发起一轮工具调用, 收到工具结果(以 OK 或 ERROR 开头的消息)后再决定下一步。\n" +
            "- 若结果是 ERROR, 请修正参数后重试该工具, 不要放弃。";

        public const string DefaultGenerateSceneJson =
            "{\n" +
            "  \"scene_name\": \"攻占巴士底狱前夜\",\n" +
            "  \"scene_description\": \"1789年7月13日夜, 巴黎街头。暴风雨前的宁静, 市民们聚集在酒馆前议论局势, 远处可见巴士底狱的轮廓。\",\n" +
            "  \"requirement\": \"中世纪法国城市街道场景。需要有酒馆、街灯、木栅栏、碎石道路; 氛围紧张压抑。\",\n" +
            "  \"transmission_auchor_points\": [\n" +
            "    {\"name\": \"酒馆内部\", \"description\": \"进入酒馆内部\", \"requirement\": \"必须是酒馆建筑, 能实现室内外切换\", \"spend_time\": \"0min\"},\n" +
            "    {\"name\": \"前往巴士底狱\", \"description\": \"乘坐马车前往巴士底狱\", \"requirement\": \"必须是马车\", \"spend_time\": \"10min\"}\n" +
            "  ],\n" +
            "  \"width\": 100,\n" +
            "  \"length\": 100\n" +
            "}";

        // ================== 生命周期 ==================

        void Start()
        {
            if (autoRunOnStart) RunTest();
        }

        void OnDestroy()
        {
            cts?.Cancel();
            client?.Dispose();
            ExitExperience();
        }

        // ================== 入口 ==================

        [ContextMenu("运行场景Agent测试")]
        public async void RunTest()
        {
            if (running) { Log("[SceneAgentTest] 正在运行中, 请等待完成"); return; }
            if (settings == null) { LogError("[SceneAgentTest] 请在 Inspector 中指定 OpenAISettings"); return; }

            running = true;
            CurrentRound = 0;
            cts = new CancellationTokenSource();
            client = new OpenAIClient(settings);
            transcript = new StringBuilder();
            ExitExperience();   // 退出场景体验(归还相机)
            world = null;
            builder?.Dispose(); // 清理上一次运行残留的3D场景
            builder = null;

            string sysPrompt = string.IsNullOrWhiteSpace(systemPrompt) ? DefaultSystemPrompt : systemPrompt;
            string sceneJson = string.IsNullOrWhiteSpace(generateSceneJson) ? DefaultGenerateSceneJson : generateSceneJson;

            try
            {
                // ---- 1. 解析剧情Agent的 generate_scene 参数 ----
                var sceneArgs = MiniJson.AsDict(MiniJson.Parse(sceneJson));
                if (sceneArgs == null) throw new FormatException("generateSceneJson 不是合法的 JSON 对象: " + Shorten(sceneJson));

                string sceneName = MiniJson.GetString(sceneArgs, "scene_name", "(未命名)");
                int width = (int)(MiniJson.GetDouble(sceneArgs, "width") ?? 100);
                int length = (int)(MiniJson.GetDouble(sceneArgs, "length") ?? 100);
                var anchors = new List<string>();
                foreach (var a in MiniJson.GetList(sceneArgs, "transmission_auchor_points") ?? new List<object>())
                {
                    var ad = MiniJson.AsDict(a);
                    if (ad != null) anchors.Add(MiniJson.GetString(ad, "name", ""));
                }

                Log($"[SceneAgentTest] ===== 开始: 场景「{sceneName}」 {width}x{length}, 传送锚点: {(anchors.Count > 0 ? string.Join(", ", anchors) : "(无)")} =====");

                // ---- 2. 初始化场景世界与对话 ----
                var library = ModelLibrary.Load();
                world = new SceneWorld(sceneName, width, length, anchors, library);
                world.ToolExecuted += OnToolExecuted;
                world.Pipeline = new ModelGenerationPipeline(client); // generate_model 真实生成管线
                world.Info += Log;
                builder = SceneBuilder3D.Attach(world); // 3D 场景构建: 每次工具调用成功后重建

                var messages = new List<ChatMessage>
                {
                    ChatMessage.System(sysPrompt),
                    ChatMessage.User(BuildUserInput(sceneArgs)),
                };
                var options = new ChatOptions
                {
                    // 温度 / max_tokens 等一律用服务端默认值
                    tools = SceneToolDefinitions.All,
                    toolChoice = "auto",
                };

                // ---- 3. Function Calling 循环(直到 finish_scene / 出错 / 手动停止) ----
                bool ok = false;
                int round = 0;
                while (true)
                {
                    round++;
                    CurrentRound = round;
                    cts.Token.ThrowIfCancellationRequested();
                    Log($"[SceneAgentTest] ---- 第 {round} 轮 ----");

                    ChatResponse resp = await client.ChatAsync(messages, options: options, cancellationToken: cts.Token);
                    var msg = resp.choices?[0]?.message;
                    string content = msg?.content?.Trim() ?? "";

                    // assistant 消息(可能携带 tool_calls)原样回传, 保持对话上下文
                    messages.Add(new ChatMessage { role = "assistant", content = content, tool_calls = msg?.tool_calls });

                    var toolCalls = msg?.tool_calls;
                    if (toolCalls == null || toolCalls.Length == 0)
                    {
                        Log($"[SceneAgentTest] [LLM] (未发起工具调用) {Shorten(content, 400)}");
                        messages.Add(ChatMessage.User("请通过工具调用继续场景生成(若全部完成则调用 finish_scene), 不要只输出文字。"));
                        continue;
                    }

                    Log($"[SceneAgentTest] [LLM] 发起 {toolCalls.Length} 个工具调用" +
                        (string.IsNullOrEmpty(content) ? "" : $": {Shorten(content, 300)}"));

                    // 逐个执行工具调用, 并以 role=tool 回传结果
                    foreach (var tc in toolCalls)
                    {
                        cts.Token.ThrowIfCancellationRequested();
                        string tool = tc.function?.name ?? "";
                        Dictionary<string, object> args;
                        try
                        {
                            args = MiniJson.AsDict(MiniJson.Parse(tc.function?.arguments ?? "{}")) ?? new Dictionary<string, object>();
                        }
                        catch (Exception parseErr)
                        {
                            string err = $"ERROR: 工具参数不是合法 JSON: {parseErr.Message}";
                            Log($"[SceneAgentTest] [工具 {tool}] {err}");
                            messages.Add(ChatMessage.Tool(tc.id, err));
                            continue;
                        }

                        AppendTranscript($"[LLM 第{round}轮调用 {tool}] {tc.function?.arguments}\n");
                        string result = await world.ExecuteToolCallAsync(tool, args, cts.Token);
                        AppendTranscript($"[工具 {tool} 结果] {result}\n");
                        messages.Add(ChatMessage.Tool(tc.id, result));
                    }

                    if (world.Finished) { ok = true; break; }
                }

                // ---- 4. 输出最终场景 ----
                Log(ok
                    ? "[SceneAgentTest] ===== 场景生成完成 =====\n" + world.DescribeScene()
                    : "[SceneAgentTest] ===== 达到最大轮数, 场景未完结 =====\n" + world.DescribeScene());
                AppendTranscript(world.DescribeScene());
            }
            catch (OperationCanceledException) { Log("[SceneAgentTest] 已取消"); }
            catch (Exception e) { LogError("[SceneAgentTest] 运行失败: " + e.Message); }
            finally
            {
                if (world != null) { world.ToolExecuted -= OnToolExecuted; world.Info -= Log; }
                // 注意: builder 不在此销毁, 让已生成的3D场景留在场景中供查看; 下次运行会整体替换
                running = false;
            }
        }

        [ContextMenu("停止测试")]
        public void StopTest() => cts?.Cancel();

        [ContextMenu("输出完整场景记录")]
        public void DumpTranscript() => Log("[SceneAgentTest] 完整记录:\n" + Transcript);

        // ================== 内部 ==================

        bool running;
        bool testingPipeline;
        SceneWorld world;
        SceneBuilder3D builder;
        GameObject experiencePlayer;

        // ================== 场景体验 ==================

        [ContextMenu("进入场景体验")]
        public void EnterExperience()
        {
            if (world == null) { Log("[SceneAgentTest] 尚无场景, 请先运行测试"); return; }
            if (experiencePlayer != null) return;
            var cam = Camera.main;
            if (cam == null) { LogError("[SceneAgentTest] 场景中没有 Main Camera"); return; }

            // 出生点: 玩家起始点, 否则场景中心
            double sx = world.HasStartPoint ? world.StartX : world.Width * 0.5;
            double sy = world.HasStartPoint ? world.StartY : world.Length * 0.5;
            float spawnY = (float)world.TerrainHeight(sx, sy) + 1.2f;

            experiencePlayer = new GameObject("SceneExperiencePlayer");
            experiencePlayer.transform.position = new Vector3((float)sx, spawnY, (float)sy);
            var cc = experiencePlayer.AddComponent<CharacterController>();
            cc.height = 1.8f;
            cc.radius = 0.35f;
            cc.center = new Vector3(0, 0.9f, 0);
            cc.slopeLimit = 50f;
            experiencePlayer.AddComponent<SceneExperienceController>().Begin(cam);
            Log("[SceneAgentTest] 进入场景体验: WASD移动 鼠标视角 Shift加速 空格跳跃 Esc退出");
        }

        [ContextMenu("退出场景体验")]
        public void ExitExperience()
        {
            if (experiencePlayer == null) return;
            var ctrl = experiencePlayer.GetComponent<SceneExperienceController>();
            if (ctrl != null) ctrl.End(); // End 内部销毁 gameObject
            experiencePlayer = null;
        }

        static string BuildUserInput(Dictionary<string, object> sceneArgs)
        {
            // 按 ARCHITECT.md §4.2, 场景Agent收到的输入: 场景描述、场景要求、传送锚点列表、场景大小
            var anchors = MiniJson.GetList(sceneArgs, "transmission_auchor_points") ?? new List<object>();
            var sb = new StringBuilder();
            sb.AppendLine("剧情Agent调用了 generate_scene, 参数如下:");
            sb.AppendLine("场景名称: " + MiniJson.GetString(sceneArgs, "scene_name", ""));
            sb.AppendLine("场景描述: " + MiniJson.GetString(sceneArgs, "scene_description", ""));
            sb.AppendLine("场景要求: " + MiniJson.GetString(sceneArgs, "requirement", ""));
            sb.AppendLine($"场景大小: width={MiniJson.GetDouble(sceneArgs, "width")}, length={MiniJson.GetDouble(sceneArgs, "length")}");
            sb.AppendLine("传送锚点列表:");
            foreach (var a in anchors)
            {
                var d = MiniJson.AsDict(a);
                if (d == null) continue;
                sb.AppendLine($"  - 名称={MiniJson.GetString(d, "name", "")} | 描述={MiniJson.GetString(d, "description", "")} | " +
                              $"要求={MiniJson.GetString(d, "requirement", "")} | 时间={MiniJson.GetString(d, "spend_time", "")}");
            }
            sb.AppendLine("请开始生成场景(第一步: init_terrain)。");
            return sb.ToString();
        }

        void OnToolExecuted(string tool, string result, SceneWorld w)
        {
            Log($"[SceneAgentTest] [工具 {tool}] {Shorten(result, 800)}");
            // 每一步工具调用后输出当前场景快照, 实现"逐步输出一个场景"
            Log("[SceneAgentTest] [当前场景]\n" + w.DescribeScene());
        }

        void AppendTranscript(string s) => transcript?.Append(s);

        static string Shorten(string s, int max = 200)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\n", " ").Replace("\r", "");
            return s.Length > max ? s.Substring(0, max) + "…" : s;
        }

        static void Log(string msg) => Debug.Log(msg);
        static void LogError(string msg) => Debug.LogError(msg);
    }
}
