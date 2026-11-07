using EpicSpark.AI.OpenAI;
using UnityEngine;

namespace EpicSpark.AI.SceneAgentTest
{
    /// <summary>
    /// 场景Agent测试面板(运行时 IMGUI, 无需在 Unity IDE / Inspector 中配置)。
    /// 挂在场景任意物体上即可; 若场景中没有 SceneAgentRunner 会自动创建一个。
    ///
    /// 左侧面板内容:
    ///   1. 模型端点信息 —— Base URL / API Key / 模型名 / 超时
    ///   2. 系统提示词与 generate_scene 参数(留空使用内置默认)
    ///   3. 运行控制 —— 运行 / 停止 / 清空记录
    ///   4. 逐步输出日志 —— 每次工具调用与当前场景快照
    /// 所有输入通过 PlayerPrefs 持久化, 重启后保留。
    /// </summary>
    public class SceneAgentTestPanel : MonoBehaviour
    {
        const float PanelWidth = 420f;
        const int LogTailChars = 6000;

        // ---- 输入内容(提示词与场景参数默认预填范例, 可直接修改) ----
        string baseUrl = "https://ai.gitee.com/v1";
        string apiKey = "";
        string model = "";
        string timeoutSeconds = "120";
        string sysPrompt = SceneAgentRunner.DefaultSystemPrompt;
        string sceneJson = SceneAgentRunner.DefaultGenerateSceneJson;
        bool showApiKey;

        Vector2 scroll;
        SceneAgentRunner runner;
        GUIStyle headingStyle, subHeadingStyle, statusStyle, logStyle;
        bool stylesReady;

        // ================== 生命周期 ==================

        void Awake()
        {
            LoadPrefs();
            EnsureRunner();
        }

        void OnDestroy() => SavePrefs();

        void EnsureRunner()
        {
            runner = FindObjectOfType<SceneAgentRunner>();
            if (runner == null)
            {
                var go = new GameObject("SceneAgentRunner");
                runner = go.AddComponent<SceneAgentRunner>();
            }
        }

        // ================== GUI ==================

        void OnGUI()
        {
            PrepareStyles();

            GUILayout.BeginArea(new Rect(10, 10, PanelWidth, Screen.height - 20), GUI.skin.box);
            scroll = GUILayout.BeginScrollView(scroll);

            GUILayout.Label("场景Agent 测试面板", headingStyle);
            DrawEndpointSection();
            DrawPromptSection();
            DrawControlSection();
            DrawLogSection();

            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        void DrawEndpointSection()
        {
            GUILayout.Label("模型端点", subHeadingStyle);
            baseUrl = TextField("Base URL", baseUrl);
            if (showApiKey)
            {
                apiKey = TextField("API Key", apiKey);
            }
            else
            {
                // 掩码显示; 输入框内仍是真实值
                string masked = new string('*', Mathf.Min(apiKey.Length, 32));
                string edited = TextField("API Key", masked);
                if (edited != masked) apiKey = edited; // 用户开始输入时以输入为准
            }
            showApiKey = GUILayout.Toggle(showApiKey, "显示 API Key");
            model = TextField("模型名", model);
            timeoutSeconds = TextField("超时(秒)", timeoutSeconds);
        }

        void DrawPromptSection()
        {
            GUILayout.Label("系统提示词 (预填范例, 可直接修改)", subHeadingStyle);
            sysPrompt = ScrollTextArea(sysPrompt, ref sysPromptScroll, ref sysPromptRect, 130);

            GUILayout.Space(6);
            GUILayout.Label("剧情Agent generate_scene 参数 (JSON, 预填范例, 可直接修改)", subHeadingStyle);
            sceneJson = ScrollTextArea(sceneJson, ref sceneJsonScroll, ref sceneJsonRect, 160);

            GUILayout.Space(4);
            GUILayout.Label("(温度 / max_tokens / 轮数均由服务端默认值控制)", statusStyle);
        }

        void DrawControlSection()
        {
            GUILayout.Space(6);
            GUILayout.BeginHorizontal();
            GUI.enabled = runner != null && !runner.IsRunning;
            if (GUILayout.Button("▶ 运行测试", GUILayout.Height(28))) RunTest();
            GUI.enabled = runner != null && runner.IsRunning;
            if (GUILayout.Button("■ 停止", GUILayout.Height(28))) runner.StopTest();
            GUI.enabled = runner != null && !runner.IsRunning;
            if (GUILayout.Button("清空记录", GUILayout.Height(28))) runner.ClearTranscript();
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            // ---- 场景体验 ----
            GUILayout.BeginHorizontal();
            GUI.enabled = runner != null && runner.HasScene && !runner.IsRunning && !runner.IsExperiencing;
            if (GUILayout.Button("🚶 进入场景体验", GUILayout.Height(28))) runner.EnterExperience();
            GUI.enabled = runner != null && runner.IsExperiencing;
            if (GUILayout.Button("退出体验", GUILayout.Height(28))) runner.ExitExperience();
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            if (runner != null && runner.IsExperiencing)
                GUILayout.Label("体验中: WASD移动 / 鼠标视角 / Shift加速 / 空格跳跃 / Esc退出", statusStyle);

            string status = runner == null ? "错误: 未找到 SceneAgentRunner"
                : runner.IsRunning ? $"运行中… 第 {runner.CurrentRound} 轮"
                : "空闲";
            GUILayout.Label(status, statusStyle);
        }

        void DrawLogSection()
        {
            GUILayout.Space(6);
            GUILayout.Label("逐步输出 (工具调用 / 场景快照)", subHeadingStyle);
            string log = runner != null ? runner.Transcript : "";
            if (log.Length > LogTailChars) log = "…(仅显示末尾)\n" + log.Substring(log.Length - LogTailChars);
            if (log.Length == 0) log = "(暂无输出, 点击 \"运行测试\" 开始)";
            GUILayout.TextArea(log, logStyle, GUILayout.MinHeight(260), GUILayout.ExpandHeight(true));
        }

        // ================== 运行 ==================

        void RunTest()
        {
            if (runner == null) EnsureRunner();
            if (runner == null) { Debug.LogError("[SceneAgentTestPanel] SceneAgentRunner 不存在"); return; }

            // 用面板输入构造运行时配置(替代 Inspector 中的 OpenAISettings 资源)
            var settings = ScriptableObject.CreateInstance<OpenAISettings>();
            settings.baseUrl = baseUrl;
            settings.apiKey = apiKey;
            settings.model = model;
            settings.timeoutSeconds = int.TryParse(timeoutSeconds, out int t) && t > 0 ? t : 120;
            runner.settings = settings;

            runner.systemPrompt = sysPrompt;
            runner.generateSceneJson = sceneJson;

            SavePrefs();
            runner.ClearTranscript();
            runner.RunTest();
        }

        // ================== 辅助 ==================

        string TextField(string label, string value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(PanelWidth * 0.32f));
            string result = GUILayout.TextField(value ?? "");
            GUILayout.EndHorizontal();
            return result;
        }

        // ---- 输入框内部滚动状态 ----
        Vector2 sysPromptScroll, sceneJsonScroll;
        Rect sysPromptRect, sceneJsonRect;
        GUIStyle textAreaStyle;

        /// <summary>
        /// 带内部滚动条的多行输入框。
        /// IMGUI 的 ScrollView 不消费滚轮事件(会冒泡到外层面板滚动), 因此:
        /// 鼠标悬停在输入框上时, 先手动消费滚轮事件并作用于内层滚动条; 否则事件留给外层面板。
        /// </summary>
        string ScrollTextArea(string value, ref Vector2 scroll, ref Rect lastRect, float height)
        {
            if (textAreaStyle == null)
                textAreaStyle = new GUIStyle(GUI.skin.textArea) { wordWrap = true };

            var e = Event.current;
            if (e.type == EventType.ScrollWheel && lastRect.Contains(e.mousePosition))
            {
                scroll.y += e.delta.y * 20f; // 手动滚动内层 ScrollView
                e.Use();                     // 阻止滚轮冒泡到外层面板
            }

            scroll = GUILayout.BeginScrollView(scroll, false, true, GUILayout.Height(height));
            value = GUILayout.TextArea(value ?? "", textAreaStyle,
                GUILayout.Width(PanelWidth - 55), GUILayout.ExpandHeight(true));
            GUILayout.EndScrollView();

            if (Event.current.type == EventType.Repaint)
                lastRect = GUILayoutUtility.GetLastRect(); // 供下一帧判断鼠标悬停位置
            return value;
        }


        void PrepareStyles()
        {
            if (stylesReady) return;
            stylesReady = true;
            headingStyle = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold };
            subHeadingStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, fontStyle = FontStyle.Bold };
            statusStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Italic };
            logStyle = new GUIStyle(GUI.skin.textArea) { wordWrap = true, fontSize = 11 };
        }

        // ================== 持久化 ==================

        const string PrefsPrefix = "SceneAgentTestPanel.";

        void LoadPrefs()
        {
            baseUrl = PlayerPrefs.GetString(PrefsPrefix + "baseUrl", baseUrl);
            apiKey = PlayerPrefs.GetString(PrefsPrefix + "apiKey", apiKey);
            model = PlayerPrefs.GetString(PrefsPrefix + "model", model);
            timeoutSeconds = PlayerPrefs.GetString(PrefsPrefix + "timeout", timeoutSeconds);
            // 预填范例: 无历史输入时填入内置默认, 用户可直接在基础上修改
            sysPrompt = PlayerPrefs.GetString(PrefsPrefix + "sysPrompt", SceneAgentRunner.DefaultSystemPrompt);
            sceneJson = PlayerPrefs.GetString(PrefsPrefix + "sceneJson", SceneAgentRunner.DefaultGenerateSceneJson);
            // 兼容旧版本存空的记录: 空白时回落到范例
            if (string.IsNullOrWhiteSpace(sysPrompt)) sysPrompt = SceneAgentRunner.DefaultSystemPrompt;
            if (string.IsNullOrWhiteSpace(sceneJson)) sceneJson = SceneAgentRunner.DefaultGenerateSceneJson;
        }

        void SavePrefs()
        {
            PlayerPrefs.SetString(PrefsPrefix + "baseUrl", baseUrl ?? "");
            PlayerPrefs.SetString(PrefsPrefix + "apiKey", apiKey ?? "");
            PlayerPrefs.SetString(PrefsPrefix + "model", model ?? "");
            PlayerPrefs.SetString(PrefsPrefix + "timeout", timeoutSeconds ?? "");
            PlayerPrefs.SetString(PrefsPrefix + "sysPrompt", sysPrompt ?? "");
            PlayerPrefs.SetString(PrefsPrefix + "sceneJson", sceneJson ?? "");
            PlayerPrefs.Save();
        }
    }
}
