// 设置界面：让玩家自己填 API Key、Base URL、各环节模型名、超时与 token 上限，并能一键测试连通性。
// 纯代码构建 uGUI（和标题界面同一套工具函数），不依赖预制体与 TMP。
using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using EpicSpark.AI.OpenAI;

namespace EpicSpark.Bootstrap
{
    /// <summary>标题界面与游戏内（F10）都能打开的模型端点设置面板。</summary>
    public class ApiSettingsScreen : MonoBehaviour
    {
        /// <summary>保存并应用后触发，参数为新的配置。</summary>
        public event Action<ApiConfig> Saved;

        /// <summary>面板关闭后触发（无论保存还是取消）。</summary>
        public event Action Closed;

        private GameObject _root;
        private InputField _keyField;
        private InputField _urlField;
        private InputField _chatField;
        private InputField _embedField;
        private InputField _imageField;
        private InputField _model3dField;
        private InputField _timeoutField;
        private InputField _maxTokenField;
        private Button _offlineButton;
        private Text _offlineLabel;
        private Button _knowledgeButton;
        private Text _knowledgeLabel;
        private Button _testButton;
        private Text _status;
        private ApiConfig _editing;
        private bool _offline;
        private bool _knowledgeRemote;
        private bool _testing;

        public bool IsVisible { get { return _root != null && _root.activeSelf; } }

        // ─────────────────────────── 生命周期 ───────────────────────────

        /// <summary>打开面板并回填当前配置。</summary>
        public void Show(ApiConfig config)
        {
            BuildIfNeeded();
            _editing = config != null ? config.Clone() : ApiConfig.Load();
            _offline = _editing.offline;
            _knowledgeRemote = _editing.knowledgeRemoteEmbedding;

            _keyField.text = _editing.apiKey ?? "";
            _urlField.text = _editing.ResolvedBaseUrl;
            _chatField.text = _editing.ResolvedChatModel;
            _embedField.text = _editing.ResolvedEmbedModel;
            _imageField.text = _editing.ResolvedImageModel;
            _model3dField.text = _editing.Resolved3DModel;
            _timeoutField.text = _editing.ResolvedTimeoutSeconds.ToString();
            _maxTokenField.text = _editing.ResolvedMaxTokens.ToString();
            RefreshToggles();
            SetStatus("Key 只保存在本机 PlayerPrefs，不会写入工程或上传。留空 Key 即以离线确定性模式游玩。");

            _root.SetActive(true);
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        public void Hide()
        {
            if (_root != null) _root.SetActive(false);
            Action handler = Closed;
            if (handler != null) handler();
        }

        public void Toggle(ApiConfig config)
        {
            if (IsVisible) Hide();
            else Show(config);
        }

        private void SetStatus(string text)
        {
            if (_status != null) _status.text = text ?? "";
        }

        private void RefreshToggles()
        {
            if (_offlineLabel != null)
            {
                _offlineLabel.text = _offline ? "离线：开" : "离线：关";
                _offlineLabel.color = _offline
                    ? new Color(0.96f, 0.72f, 0.28f)
                    : new Color(0.70f, 0.94f, 0.82f);
            }
            if (_offlineButton != null) ApplyToggleColors(_offlineButton, _offline ? 1 : 0);

            if (_knowledgeLabel != null)
            {
                _knowledgeLabel.text = _knowledgeRemote ? "知识库远端向量：开" : "知识库远端向量：关";
                _knowledgeLabel.color = _knowledgeRemote
                    ? new Color(0.96f, 0.72f, 0.28f)
                    : new Color(0.70f, 0.94f, 0.82f);
            }
            if (_knowledgeButton != null) ApplyToggleColors(_knowledgeButton, _knowledgeRemote ? 1 : 0);
        }

        private static void ApplyToggleColors(Button button, int state)
        {
            var colors = button.colors;
            if (state == 1)
            {
                colors.normalColor = new Color(0.34f, 0.26f, 0.10f, 1f);
                colors.highlightedColor = new Color(0.44f, 0.34f, 0.14f, 1f);
            }
            else
            {
                colors.normalColor = new Color(0.14f, 0.24f, 0.20f, 1f);
                colors.highlightedColor = new Color(0.20f, 0.34f, 0.28f, 1f);
            }
            button.colors = colors;
        }

        // ─────────────────────────── 交互 ───────────────────────────

        private void OnToggleOffline()
        {
            _offline = !_offline;
            RefreshToggles();
        }

        private void OnToggleKnowledgeRemote()
        {
            _knowledgeRemote = !_knowledgeRemote;
            RefreshToggles();
            if (_knowledgeRemote)
            {
                SetStatus("注意：开启后首次检索会用远端 /embeddings 给全部 3600+ 文本块建索引，" +
                          "每批 16 条、可能数百次请求（数分钟）。只想快速游玩请保持关闭。");
            }
        }

        private void OnPreset(string preset)
        {
            if (preset == "gitee")
            {
                _urlField.text = ApiConfig.DefaultBaseUrl;
                _chatField.text = ApiConfig.DefaultChatModel;
                _embedField.text = ApiConfig.DefaultEmbedModel;
                _imageField.text = ApiConfig.DefaultImageModel;
                _model3dField.text = ApiConfig.Default3DModel;
                _knowledgeRemote = false;
                SetStatus("已填入 Gitee AI 默认值：聊天 + 向量 + 文生图 + 生 3D 都在同一端点。");
            }
            else if (preset == "deepseek")
            {
                _urlField.text = "https://api.deepseek.com/v1";
                _chatField.text = "deepseek-chat";
                _embedField.text = "";
                _imageField.text = "";
                _model3dField.text = "";
                _knowledgeRemote = false;
                SetStatus("已填入 DeepSeek 官方：只有对话可用（它没有 /embeddings 与生图），" +
                          "知识库保持本地向量，缺失资产会用库内模型克隆。");
            }
            else if (preset == "local")
            {
                _urlField.text = "http://127.0.0.1:1234/v1";
                _chatField.text = "local-model";
                _embedField.text = "text-embedding-nomic-embed-text-v1.5";
                _knowledgeRemote = false;
                SetStatus("已填入本地 OpenAI 兼容端点（LM Studio / Ollama 常见地址），本地端点一般不需要 Key。");
            }
            RefreshToggles();
        }

        private void OnResetClicked()
        {
            var defaults = ApiConfig.Defaults();
            ApplyToFields(defaults);
            _knowledgeRemote = false;
            RefreshToggles();
            SetStatus("已填入默认值，点「保存并应用」生效（或点「清除本机配置」彻底回到 Inspector 设置）。");
        }

        private void OnClearClicked()
        {
            ApiConfig.ClearSaved();
            var defaults = ApiConfig.Defaults();
            ApplyToFields(defaults);
            _knowledgeRemote = false;
            RefreshToggles();
            SetStatus("本机保存的配置已清除，重新打开游戏将回落到 Inspector / 环境变量。");
        }

        private void ApplyToFields(ApiConfig cfg)
        {
            _keyField.text = cfg.apiKey ?? "";
            _urlField.text = cfg.ResolvedBaseUrl;
            _chatField.text = cfg.ResolvedChatModel;
            _embedField.text = cfg.ResolvedEmbedModel;
            _imageField.text = cfg.ResolvedImageModel;
            _model3dField.text = cfg.Resolved3DModel;
            _timeoutField.text = cfg.ResolvedTimeoutSeconds.ToString();
            _maxTokenField.text = cfg.ResolvedMaxTokens.ToString();
            _offline = cfg.offline;
        }

        private ApiConfig ReadFields()
        {
            var cfg = _editing != null ? _editing.Clone() : new ApiConfig();
            cfg.apiKey = Trim(_keyField.text);
            cfg.baseUrl = Trim(_urlField.text);
            cfg.chatModel = Trim(_chatField.text);
            cfg.embedModel = Trim(_embedField.text);
            cfg.imageModel = Trim(_imageField.text);
            cfg.model3d = Trim(_model3dField.text);
            cfg.offline = _offline;
            cfg.knowledgeRemoteEmbedding = _knowledgeRemote;
            cfg.chatTimeoutSeconds = ParseInt(_timeoutField.text, ApiConfig.DefaultTimeoutSeconds);
            cfg.maxTokens = ParseInt(_maxTokenField.text, ApiConfig.DefaultMaxTokens);
            return cfg;
        }

        private void OnSaveClicked()
        {
            ApiConfig cfg = ReadFields();
            if (!cfg.HasKey && !cfg.offline)
            {
                SetStatus("没有填 API Key：将以离线确定性模式运行（四个 Agent 全部用本地兜底）。");
            }
            cfg.Save();
            _editing = cfg;
            ApiConfig applied = cfg.Clone();
            Hide();
            Action<ApiConfig> handler = Saved;
            if (handler != null) handler(applied);
        }

        /// <summary>用当前界面上的值真发一次对话与向量请求，把状态码 / 耗时 / 错误直接显示出来。</summary>
        private async void OnTestClicked()
        {
            if (_testing) return;
            ApiConfig cfg = ReadFields();
            if (!cfg.HasKey)
            {
                SetStatus("没有 API Key，无法测试远程连接（离线模式本来就不发请求）。");
                return;
            }

            _testing = true;
            if (_testButton != null) _testButton.interactable = false;
            SetStatus("正在测试对话模型 " + cfg.ResolvedChatModel + " …");

            OpenAISettings settings = ScriptableObject.CreateInstance<OpenAISettings>();
            settings.baseUrl = cfg.ResolvedBaseUrl;
            settings.apiKey = cfg.apiKey;
            settings.chatModel = cfg.ResolvedChatModel;
            settings.embedModel = cfg.ResolvedEmbedModel;
            settings.timeoutSeconds = Mathf.Clamp(cfg.ResolvedTimeoutSeconds, 5, 180);
            settings.maxRetries = 1;
            settings.maxTokens = Mathf.Max(256, cfg.ResolvedMaxTokens);
            settings.verboseLogging = true;

            var client = new OpenAIClient(settings);
            string chatLine;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                string reply = await client.ChatContentAsync("你是连接测试助手。", "只回复两个字：收到", 0f, CancellationToken.None);
                watch.Stop();
                chatLine = reply != null
                    ? "✅ 对话 OK：" + Truncate(reply, 18) + "（" + watch.ElapsedMilliseconds + "ms）"
                    : "❌ 对话失败：" + client.LastError;
            }
            catch (Exception e)
            {
                watch.Stop();
                chatLine = "❌ 对话异常：" + e.Message;
            }

            SetStatus(chatLine + "\n正在测试向量模型 " + cfg.ResolvedEmbedModel + " …");
            string embedLine;
            var watch2 = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                float[] vector = string.IsNullOrEmpty(cfg.embedModel)
                    ? null
                    : await client.EmbedAsync("连接测试", CancellationToken.None);
                watch2.Stop();
                if (string.IsNullOrEmpty(cfg.embedModel))
                    embedLine = "⚪ 向量：未填模型名，已跳过（知识库将用本地向量）";
                else if (vector == null)
                    embedLine = "❌ 向量失败：" + client.LastError + "（不影响离线游玩，保持「知识库远端向量」关闭即可）";
                else
                    embedLine = "✅ 向量 OK：维度 " + vector.Length + "（" + watch2.ElapsedMilliseconds + "ms）";
            }
            catch (Exception e)
            {
                watch2.Stop();
                embedLine = "❌ 向量异常：" + e.Message;
            }

            SetStatus(chatLine + "\n" + embedLine);
            if (_testButton != null) _testButton.interactable = true;
            _testing = false;
            Destroy(settings);
        }

        private static int ParseInt(string text, int fallback)
        {
            int value;
            if (!string.IsNullOrEmpty(text) && int.TryParse(text.Trim(), out value)) return value;
            return fallback;
        }

        private static string Truncate(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string flat = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
            return flat.Length <= max ? flat : flat.Substring(0, max) + "…";
        }

        private static string Trim(string value)
        {
            return value == null ? "" : value.Trim();
        }

        // ─────────────────────────── UI 构造 ───────────────────────────

        private void BuildIfNeeded()
        {
            if (_root != null) return;
            EnsureEventSystem();

            var canvasGo = new GameObject("ApiSettingsCanvas");
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 120;
            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            canvasGo.AddComponent<GraphicRaycaster>();
            _root = canvasGo;

            // 半透明遮罩，挡住背后的点击
            GameObject dim = TitleScreen.CreateRect("Dim", _root.transform, new Color(0f, 0f, 0f, 0.82f));
            StretchFull(dim);
            dim.GetComponent<Image>().raycastTarget = true;

            // 面板
            GameObject panel = TitleScreen.CreateRect("Panel", _root.transform, new Color(0.09f, 0.11f, 0.13f, 1f));
            var panelRt = panel.GetComponent<RectTransform>();
            panelRt.anchorMin = new Vector2(0.5f, 0.5f);
            panelRt.anchorMax = new Vector2(0.5f, 0.5f);
            panelRt.sizeDelta = new Vector2(1140f, 940f);
            panelRt.anchoredPosition = Vector2.zero;
            panel.GetComponent<Image>().raycastTarget = true;

            var accent = TitleScreen.CreateRect("Accent", panel.transform, new Color(0.16f, 0.82f, 0.74f, 1f));
            var accentRt = accent.GetComponent<RectTransform>();
            accentRt.anchorMin = new Vector2(0.5f, 0.5f);
            accentRt.anchorMax = new Vector2(0.5f, 0.5f);
            accentRt.sizeDelta = new Vector2(1000f, 3f);
            accentRt.anchoredPosition = new Vector2(0f, 360f);

            Text title = TitleScreen.CreateText("Title", panel.transform, "模型与 API 设置", 42,
                new Color(0.94f, 0.95f, 0.96f));
            title.fontStyle = FontStyle.Bold;
            SetRect(title.rectTransform, new Vector2(0f, 418f), new Vector2(1000f, 60f));

            Text subtitle = TitleScreen.CreateText("Subtitle", panel.transform,
                "可自定义 OpenAI 兼容端点与各环节模型；不填 Key 就是离线确定性模式。", 22,
                new Color(0.60f, 0.66f, 0.70f));
            SetRect(subtitle.rectTransform, new Vector2(0f, 374f), new Vector2(1000f, 40f));

            _keyField = AddInput(panel.transform, "API Key", 322f, "服务商提供的密钥", 0);
            _urlField = AddInput(panel.transform, "Base URL", 266f, "https://ai.gitee.com/v1", 200);
            _chatField = AddInput(panel.transform, "对话模型", 210f, ApiConfig.DefaultChatModel, 120);
            _embedField = AddInput(panel.transform, "向量模型", 154f, ApiConfig.DefaultEmbedModel, 120);
            _imageField = AddInput(panel.transform, "文生图模型", 98f, ApiConfig.DefaultImageModel, 120);
            _model3dField = AddInput(panel.transform, "单图生 3D 模型", 42f, ApiConfig.Default3DModel, 120);
            _timeoutField = AddInput(panel.transform, "请求超时(秒)", -14f, ApiConfig.DefaultTimeoutSeconds.ToString(), 4);
            _maxTokenField = AddInput(panel.transform, "max_tokens", -70f, ApiConfig.DefaultMaxTokens.ToString(), 6);

            _offlineButton = TitleScreen.CreateButton("OfflineButton", panel.transform, "离线：关",
                new Vector2(110f, -126f), new Vector2(700f, 44f),
                new Color(0.14f, 0.24f, 0.20f, 1f), new Color(0.20f, 0.34f, 0.28f, 1f));
            _offlineButton.onClick.AddListener(OnToggleOffline);
            _offlineLabel = _offlineButton.GetComponentInChildren<Text>();

            _knowledgeButton = TitleScreen.CreateButton("KnowledgeButton", panel.transform, "知识库远端向量：关",
                new Vector2(110f, -176f), new Vector2(700f, 44f),
                new Color(0.14f, 0.24f, 0.20f, 1f), new Color(0.20f, 0.34f, 0.28f, 1f));
            _knowledgeButton.onClick.AddListener(OnToggleKnowledgeRemote);
            _knowledgeLabel = _knowledgeButton.GetComponentInChildren<Text>();

            // 预设
            Text presetLabel = TitleScreen.CreateText("PresetLabel", panel.transform, "预设", 22,
                new Color(0.72f, 0.78f, 0.82f));
            presetLabel.alignment = TextAnchor.MiddleRight;
            SetRect(presetLabel.rectTransform, new Vector2(-470f, -230f), new Vector2(430f, 40f));

            var gitee = TitleScreen.CreateButton("PresetGitee", panel.transform, "Gitee AI",
                new Vector2(-100f, -230f), new Vector2(210f, 40f),
                new Color(0.15f, 0.18f, 0.22f, 1f), new Color(0.22f, 0.27f, 0.32f, 1f));
            gitee.onClick.AddListener(delegate { OnPreset("gitee"); });

            var deepseek = TitleScreen.CreateButton("PresetDeepSeek", panel.transform, "DeepSeek 官方",
                new Vector2(130f, -230f), new Vector2(210f, 40f),
                new Color(0.15f, 0.18f, 0.22f, 1f), new Color(0.22f, 0.27f, 0.32f, 1f));
            deepseek.onClick.AddListener(delegate { OnPreset("deepseek"); });

            var local = TitleScreen.CreateButton("PresetLocal", panel.transform, "本地 LM Studio",
                new Vector2(360f, -230f), new Vector2(210f, 40f),
                new Color(0.15f, 0.18f, 0.22f, 1f), new Color(0.22f, 0.27f, 0.32f, 1f));
            local.onClick.AddListener(delegate { OnPreset("local"); });

            _status = TitleScreen.CreateText("Status", panel.transform, "", 20, new Color(0.66f, 0.74f, 0.78f));
            _status.alignment = TextAnchor.MiddleCenter;
            SetRect(_status.rectTransform, new Vector2(0f, -286f), new Vector2(1040f, 60f));

            _testButton = TitleScreen.CreateButton("TestButton", panel.transform, "测试连接",
                new Vector2(-270f, -352f), new Vector2(250f, 60f),
                new Color(0.16f, 0.24f, 0.34f, 1f), new Color(0.22f, 0.34f, 0.46f, 1f));
            _testButton.onClick.AddListener(OnTestClicked);

            var save = TitleScreen.CreateButton("SaveButton", panel.transform, "保存并应用",
                new Vector2(0f, -352f), new Vector2(250f, 60f),
                new Color(0.10f, 0.62f, 0.55f, 1f), new Color(0.14f, 0.78f, 0.68f, 1f));
            save.onClick.AddListener(OnSaveClicked);

            var close = TitleScreen.CreateButton("CloseButton", panel.transform, "关闭",
                new Vector2(270f, -352f), new Vector2(250f, 60f),
                new Color(0.22f, 0.18f, 0.18f, 1f), new Color(0.32f, 0.26f, 0.26f, 1f));
            close.onClick.AddListener(Hide);

            var reset = TitleScreen.CreateButton("ResetButton", panel.transform, "恢复默认值",
                new Vector2(-130f, -414f), new Vector2(230f, 38f),
                new Color(0.14f, 0.14f, 0.17f, 1f), new Color(0.20f, 0.20f, 0.24f, 1f));
            reset.onClick.AddListener(OnResetClicked);

            var clear = TitleScreen.CreateButton("ClearButton", panel.transform, "清除本机配置",
                new Vector2(130f, -414f), new Vector2(230f, 38f),
                new Color(0.14f, 0.14f, 0.17f, 1f), new Color(0.20f, 0.20f, 0.24f, 1f));
            clear.onClick.AddListener(OnClearClicked);

            _root.SetActive(false);
        }

        private InputField AddInput(Transform parent, string label, float y, string placeholder, int characterLimit)
        {
            Text caption = TitleScreen.CreateText("Label_" + label, parent, label, 22, new Color(0.80f, 0.84f, 0.86f));
            caption.alignment = TextAnchor.MiddleRight;
            SetRect(caption.rectTransform, new Vector2(-470f, y), new Vector2(430f, 44f));

            var go = new GameObject("Input_" + label, typeof(RectTransform), typeof(CanvasRenderer),
                typeof(Image), typeof(InputField));
            go.transform.SetParent(parent, false);
            var image = go.GetComponent<Image>();
            image.color = new Color(0.12f, 0.14f, 0.16f, 1f);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(700f, 44f);
            rt.anchoredPosition = new Vector2(110f, y);

            Text text = TitleScreen.CreateText("Text", go.transform, "", 21, new Color(0.95f, 0.96f, 0.97f));
            var textRt = text.rectTransform;
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.offsetMin = new Vector2(14f, 5f);
            textRt.offsetMax = new Vector2(-14f, -5f);
            text.alignment = TextAnchor.MiddleLeft;
            text.supportRichText = false;

            Text ph = TitleScreen.CreateText("Placeholder", go.transform, placeholder, 21,
                new Color(0.42f, 0.46f, 0.50f));
            var phRt = ph.rectTransform;
            phRt.anchorMin = Vector2.zero;
            phRt.anchorMax = Vector2.one;
            phRt.offsetMin = new Vector2(14f, 5f);
            phRt.offsetMax = new Vector2(-14f, -5f);
            ph.alignment = TextAnchor.MiddleLeft;

            var field = go.GetComponent<InputField>();
            field.textComponent = text;
            field.placeholder = ph;
            field.targetGraphic = image;
            field.lineType = InputField.LineType.SingleLine;
            field.characterLimit = characterLimit;
            return field;
        }

        private static void SetRect(RectTransform rt, Vector2 position, Vector2 size)
        {
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = size;
            rt.anchoredPosition = position;
        }

        private static void StretchFull(GameObject go)
        {
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        private static void EnsureEventSystem()
        {
            if (UnityEngine.Object.FindObjectOfType<EventSystem>() != null) return;
            var go = new GameObject("EventSystem");
            go.AddComponent<EventSystem>();
            go.AddComponent<StandaloneInputModule>();
        }
    }
}
