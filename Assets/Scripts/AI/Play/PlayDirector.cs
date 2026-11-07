using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EpicSpark.AI.OpenAI;
using EpicSpark.AI.SceneAgentTest;
using EpicSpark.AI.StoryAgent;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace EpicSpark.AI.Play
{
    /// <summary>
    /// 剧情体验导演。流程:
    ///   开始界面(选剧本+配置API) → 按需端点生成场景模型 → 启动剧情Agent
    ///   (enter_scene/finish, 目录=剧本的预制场景) → 手工搭建所选场景 → 第一人称体验
    ///   → 走进锚点 → 剧情Agent决策 → 切换场景。
    /// 场景为预制真实Unity场景(PlaySceneBuilder), 与场景Agent无关。
    /// </summary>
    public class PlayDirector : MonoBehaviour
    {
        enum State { Menu, Generating, PlotThinking, InScene }

        // ---- 用户配置(持久化) ----
        string baseUrl = "https://ai.gitee.com/v1";
        string apiKey = "";
        string model = "";
        string timeoutSeconds = "180";
        int scriptIndex;

        State state = State.Menu;
        string busyLabel = "";
        string notice = "";

        OpenAIClient client;
        readonly Dictionary<string, string> genPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        const string SceneDir = "Assets/Scenes/Hemudu/";
        string loadedScenePath;
        GameObject player;
        Camera mainCam;
        Vector3 camMenuPos;
        Quaternion camMenuRot;
        PlayScript script;
        PlaySceneDef currentScene;
        CancellationTokenSource cts;

        // ================== 生命周期 ==================

        void Awake()
        {
            LoadPrefs();
            mainCam = Camera.main;
            if (mainCam != null) { camMenuPos = mainCam.transform.position; camMenuRot = mainCam.transform.rotation; }
        }

        void OnDestroy()
        {
            cts?.Cancel();
            client?.Dispose();
            SavePrefs();
        }

        // ================== GUI ==================

        void OnGUI()
        {
            switch (state)
            {
                case State.Menu: DrawMenu(); break;
                case State.Generating:
                case State.PlotThinking:
                    GUILayout.BeginArea(new Rect(Screen.width / 2f - 260, Screen.height / 2f - 60, 520, 120), GUI.skin.box);
                    GUILayout.Label(busyLabel, new GUIStyle(GUI.skin.label) { fontSize = 14, wordWrap = true });
                    GUILayout.EndArea();
                    break;
                case State.InScene:
                    GUILayout.BeginArea(new Rect(Screen.width - 330, 10, 320, 60));
                    GUILayout.Label("走进黄色锚点球触发传送 | WASD移动 | Esc退出体验", new GUIStyle(GUI.skin.label) { fontSize = 12 });
                    if (!string.IsNullOrEmpty(notice)) GUILayout.Label(notice, new GUIStyle(GUI.skin.label) { fontSize = 12 });
                    GUILayout.EndArea();
                    break;
            }
        }

        void DrawMenu()
        {
            float w = 460, h = 330;
            GUILayout.BeginArea(new Rect(Screen.width / 2f - w / 2, Screen.height / 2f - h / 2, w, h), GUI.skin.box);
            GUILayout.Label("EpicSpark 剧情体验", new GUIStyle(GUI.skin.label) { fontSize = 18, fontStyle = FontStyle.Bold });

            GUILayout.Label("选择剧本:");
            for (int i = 0; i < PlayScripts.All.Count; i++)
            {
                bool sel = GUILayout.Toggle(scriptIndex == i, PlayScripts.All[i].name + " — " + PlayScripts.All[i].description,
                    GUI.skin.button);
                if (sel) scriptIndex = i;
            }

            GUILayout.Space(6);
            baseUrl = Field("Base URL", baseUrl);
            apiKey = Field("API Key", apiKey);
            model = Field("模型名", model);

            GUILayout.Space(8);
            GUI.enabled = !string.IsNullOrEmpty(apiKey);
            if (GUILayout.Button("▶ 开始体验", GUILayout.Height(30))) _ = StartExperienceAsync();
            GUI.enabled = true;
            GUILayout.EndArea();
        }

        string Field(string label, string value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(90));
            string r = GUILayout.TextField(value ?? "");
            GUILayout.EndHorizontal();
            return r;
        }

        // ================== 主流程 ==================

        async Task StartExperienceAsync()
        {
            script = PlayScripts.All[Mathf.Clamp(scriptIndex, 0, PlayScripts.All.Count - 1)];
            SavePrefs();

            var settings = ScriptableObject.CreateInstance<OpenAISettings>();
            settings.baseUrl = baseUrl;
            settings.apiKey = apiKey;
            settings.model = model;
            settings.timeoutSeconds = int.TryParse(timeoutSeconds, out int t) && t > 0 ? t : 180;
            client = new OpenAIClient(settings);
            cts = new CancellationTokenSource();

            // 1. 场景模型均为内置资产; 若剧本声明了需生成的模型则调端点(幂等缓存)
            state = State.Generating;
            try
            {
                var pipeline = new ModelGenerationPipeline(client);
                var toGen = new List<PlayGenModel>();
                foreach (var s in script.scenes)
                    foreach (var g in s.genModels)
                        if (!string.IsNullOrEmpty(g.prompt) && !genPaths.ContainsKey(g.key) && toGen.Find(x => x.key == g.key) == null)
                            toGen.Add(g);

                int idx = 0;
                foreach (var g in toGen)
                {
                    idx++;
                    busyLabel = $"正在生成场景模型 ({idx}/{toGen.Count}): {g.prompt}";
                    try
                    {
                        byte[] glb = await pipeline.GenerateGlbAsync(g.prompt, null, cts.Token);
                        string dir = Path.Combine(Application.persistentDataPath, "PlayModels");
                        Directory.CreateDirectory(dir);
                        string path = Path.Combine(dir, g.key + ".glb");
                        File.WriteAllBytes(path, glb);
                        genPaths[g.key] = path;
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning("[Play] 模型生成失败 " + g.key + ": " + e.Message);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Play] 模型生成阶段异常: " + e.Message);
            }

            // 2. 剧情Agent: 选择首个场景
            var decision = await RunPlotAsync(
                $"剧本「{script.name}」开始。{script.description}\n请为玩家选择第一个场景进入。", "");
            if (decision == null || decision.gameOver)
            {
                state = State.Menu;
                notice = decision != null ? "剧情Agent直接结束了游戏。" : "剧情Agent失败。";
                return;
            }

            await EnterSceneAsync(script.scenes.Find(s => s.sceneName == decision.enterName));
        }

        /// <summary>运行剧情Agent(enter_scene/finish), 目录=剧本场景。</summary>
        async Task<PlotDecision> RunPlotAsync(string userInput, string currentSceneName)
        {
            state = State.PlotThinking;
            busyLabel = "剧情Agent推理中…";
            var catalog = new List<PlotScenePreset>();
            foreach (var s in script.scenes) catalog.Add(s.ToCatalogEntry());

            var agent = new PlotAgent { CurrentSceneName = currentSceneName ?? "" };
            try
            {
                return await agent.BeginAsync(client, userInput, "", catalog,
                    s => busyLabel = "剧情Agent: " + s, cts.Token);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Play] 剧情Agent异常: " + e.Message);
                return null;
            }
        }

        async Task EnterSceneAsync(PlaySceneDef def)
        {
            if (def == null) { Debug.LogWarning("[Play] 找不到场景定义"); return; }

            // 加载预制 .unity 场景文件(真实场景资产, Additive 保留导演/玩家)
            if (!string.IsNullOrEmpty(loadedScenePath))
            {
                var unload = SceneManager.UnloadSceneAsync(loadedScenePath);
                while (unload != null && !unload.isDone) await Task.Yield();
            }
            string path = SceneDir + def.sceneName + ".unity";
            AsyncOperation op;
#if UNITY_EDITOR
            // Editor Play 下 SceneManager 只能加载 Build Settings 中的场景;
            // 未列入的场景必须用 EditorSceneManager.LoadSceneAsyncInPlayMode (Unity 官方 API)。
            op = UnityEditor.SceneManagement.EditorSceneManager.LoadSceneAsyncInPlayMode(
                path, new LoadSceneParameters(LoadSceneMode.Additive));
#else
            op = SceneManager.LoadSceneAsync(path, LoadSceneMode.Additive);
#endif
            if (op == null)
            {
                Debug.LogWarning("[Play] 场景文件加载失败(请先用菜单 EpicSpark/AI/生成河姆渡场景资产): " + path);
                return;
            }
            while (!op.isDone) await Task.Yield();
            loadedScenePath = path;

            currentScene = def;
            notice = def.narrative;

            // 玩家
            if (player == null)
            {
                player = new GameObject("Player");
                var cc = player.AddComponent<CharacterController>();
                cc.height = 1.8f; cc.radius = 0.35f; cc.center = new Vector3(0, 0.9f, 0);
                var ctrl = player.AddComponent<SceneExperienceController>();
                ctrl.Begin(mainCam);
            }
            Teleport(def.playerSpawn);
            state = State.InScene;
        }

        void Teleport(Vector3 pos)
        {
            var cc = player.GetComponent<CharacterController>();
            cc.enabled = false;
            player.transform.position = pos;
            cc.enabled = true;
        }

        // ================== 锚点触发 ==================

        void Update()
        {
            if (state != State.InScene || player == null || currentScene == null) return;
            foreach (var a in currentScene.anchors)
            {
                Vector3 p = a.pos; p.y = player.transform.position.y;
                if ((player.transform.position - p).magnitude < 2.5f)
                {
                    _ = HandleAnchorAsync(a);
                    break;
                }
            }
        }

        async Task HandleAnchorAsync(PlayAnchorDef anchor)
        {
            if (state != State.InScene) return;
            notice = $"进入锚点「{anchor.name}」  时间推进: {anchor.spendTime}";
            var target = script.scenes.Find(s => s.sceneName == anchor.targetScene);

            // 剧情Agent决策(原地时间跳转也交给它确认)
            string user = $"玩家当前在场景「{currentScene.sceneName}」, 走进了锚点「{anchor.name}」({anchor.description}), " +
                          $"该锚点时间影响为 {anchor.spendTime}。请决定玩家接下来进入哪个场景。";
            var decision = await RunPlotAsync(user, currentScene.sceneName);
            if (decision == null) { notice = "剧情Agent决策失败, 留在当前场景。"; state = State.InScene; return; }

            if (decision.gameOver)
            {
                notice = $"游戏结束 | {decision.finishSummary} | 成就: {decision.finishAchievement}";
                state = State.Menu;
                return;
            }

            var next = script.scenes.Find(s => s.sceneName == decision.enterName);
            if (next == null) { notice = "剧情Agent给出的场景不存在, 留在当前场景。"; state = State.InScene; return; }

            if (next.sceneName == currentScene.sceneName && decision.isStillInPlace)
            {
                notice = $"时间推进: {anchor.spendTime}。(原地)";
                state = State.InScene;
                return;
            }
            await EnterSceneAsync(next);
        }

        // ================== 持久化 ==================

        const string P = "PlayDirector.";

        void LoadPrefs()
        {
            baseUrl = PlayerPrefs.GetString(P + "baseUrl", baseUrl);
            apiKey = PlayerPrefs.GetString(P + "apiKey", apiKey);
            model = PlayerPrefs.GetString(P + "model", model);
            timeoutSeconds = PlayerPrefs.GetString(P + "timeout", timeoutSeconds);
            scriptIndex = PlayerPrefs.GetInt(P + "scriptIndex", 0);
        }

        void SavePrefs()
        {
            PlayerPrefs.SetString(P + "baseUrl", baseUrl ?? "");
            PlayerPrefs.SetString(P + "apiKey", apiKey ?? "");
            PlayerPrefs.SetString(P + "model", model ?? "");
            PlayerPrefs.SetString(P + "timeout", timeoutSeconds ?? "");
            PlayerPrefs.SetInt(P + "scriptIndex", scriptIndex);
            PlayerPrefs.Save();
        }
    }
}
