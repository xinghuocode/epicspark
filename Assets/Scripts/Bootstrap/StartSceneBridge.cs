// 神殿开始场景桥接：把移植进来的 SparkCode 星火神殿开始场景接到 EpicSpark 主流程。
//
//  开始场景（StartScene）里点击「开始游戏」→ 神殿镜头聚焦星火 → 弹出历史事件输入面板
//  →「开始生成」写入 PlayerPrefs 启动参数 → 载入 EpicSpark 主场景 → GameBootstrap 自动开局。
//
// 该组件由 RuntimeInitializeOnLoadMethod 自动挂到含 StartMenuController 的场景里，
// 不需要在场景里额外摆对象；在 EpicSpark 主场景里不会生效。
using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace EpicSpark.Bootstrap
{
    /// <summary>星火神殿开始场景 → 历史事件输入 → EpicSpark 主场景。</summary>
    public class StartSceneBridge : MonoBehaviour
    {
        /// <summary>开局场景名（必须出现在 Build Settings 里）。</summary>
        public const string GameplaySceneName = "EpicSpark";

        private SparkCode.StartMenuController _menu;
        private GameObject _root;
        private InputField _input;
        private Text _status;
        private GameObject _resumeButton;
        private Button _startButton;
        private ApiSettingsScreen _apiScreen;

        private bool _overlayVisible;
        private bool _startedSeen;
        private float _startedAt = -1f;

        private Color _gold = new Color(0.84f, 0.67f, 0.36f, 1f);
        private Color _goldBright = new Color(0.96f, 0.82f, 0.54f, 1f);
        private Color _ink = new Color(0.055f, 0.060f, 0.068f, 0.92f);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoCreate()
        {
            if (UnityEngine.Object.FindObjectOfType<StartSceneBridge>() != null) return;
            if (UnityEngine.Object.FindObjectOfType<SparkCode.StartMenuController>() == null) return;
            var go = new GameObject("StartSceneBridge");
            go.AddComponent<StartSceneBridge>();
            Debug.Log("[神殿] 已挂载开始场景桥接。");
        }

        private void Awake()
        {
            _menu = UnityEngine.Object.FindObjectOfType<SparkCode.StartMenuController>();
        }

        private void OnDestroy()
        {
            if (_apiScreen != null)
            {
                _apiScreen.Saved -= OnApiSaved;
                _apiScreen.Closed -= OnApiClosed;
            }
        }

        private void Update()
        {
            if (_menu == null)
            {
                _menu = UnityEngine.Object.FindObjectOfType<SparkCode.StartMenuController>();
                if (_menu == null) return;
            }

            // 主菜单状态：重置计时，确保回到主菜单后还能再次进入。
            if (!_menu.IsStarted)
            {
                _startedSeen = false;
                _startedAt = -1f;
                if (_overlayVisible) HideOverlay();
                return;
            }

            if (!_startedSeen)
            {
                _startedSeen = true;
                _startedAt = Time.unscaledTime;
            }

            // 等菜单淡出与镜头聚焦动画走一小段，再淡入输入面板。
            if (!_overlayVisible && Time.unscaledTime - _startedAt > 0.85f)
                ShowOverlay();

            if (_overlayVisible && Input.GetKeyDown(KeyCode.Escape))
            {
                if (_apiScreen != null && _apiScreen.IsVisible)
                {
                    _apiScreen.Hide();
                    return;
                }
                HideOverlay();
                if (_menu != null) _menu.BackToMain();
            }
        }

        // 兼容旧逻辑：面板隐藏时也要把 caption 恢复，方便再次开局。
        private bool TutorialPanelHidden = false;

        // ─────────────────────────── 输入面板 ───────────────────────────

        private void ShowOverlay()
        {
            if (_root == null) BuildUi();
            _root.SetActive(true);
            _overlayVisible = true;

            if (_menu != null && _menu.captionGroup != null)
            {
                _menu.captionGroup.gameObject.SetActive(false);
                TutorialPanelHidden = true;
            }

            bool hasSave = SaveService.HasSave && SaveService.Load() != null;
            if (_resumeButton != null) _resumeButton.SetActive(hasSave);
            if (_startButton != null)
            {
                _startButton.GetComponent<RectTransform>().anchoredPosition =
                    hasSave ? new Vector2(-170f, -122f) : new Vector2(0f, -122f);
            }

            RefreshStatus(null, false);
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            if (_input != null)
            {
                _input.text = "";
                EventSystem.current.SetSelectedGameObject(_input.gameObject);
                _input.ActivateInputField();
            }
        }

        private void HideOverlay()
        {
            _overlayVisible = false;
            if (_root != null) _root.SetActive(false);
            if (_apiScreen != null) _apiScreen.Hide();
            if (_menu != null && _menu.captionGroup != null && TutorialPanelHidden)
            {
                _menu.captionGroup.gameObject.SetActive(true);
                TutorialPanelHidden = false;
            }
        }

        /// <summary>供自动化测试 / 外部调用：填入事件并直接开局。</summary>
        public void SubmitEvent(string value)
        {
            if (_root == null) BuildUi();
            if (_input != null) _input.text = value;
            ConfirmStart();
        }

        private void ConfirmStart()
        {
            string value = _input != null ? (_input.text ?? "").Trim() : "";
            if (value.Length == 0)
            {
                RefreshStatus("请先输入一个历史事件名称，或点击上方的推荐事件。", true);
                if (_input != null) _input.ActivateInputField();
                return;
            }

            if (value.Length > 40) value = value.Substring(0, 40);
            RefreshStatus("星火已点燃，正在载入「" + value + "」…", true);
            PlayerPrefs.SetString(GameBootstrap.BootEventKey, value);
            PlayerPrefs.DeleteKey(GameBootstrap.BootResumeKey);
            PlayerPrefs.Save();
            SceneManager.LoadScene(GameplaySceneName);
        }

        private void ConfirmResume()
        {
            RefreshStatus("正在读取上次存档…", true);
            PlayerPrefs.SetInt(GameBootstrap.BootResumeKey, 1);
            PlayerPrefs.DeleteKey(GameBootstrap.BootEventKey);
            PlayerPrefs.Save();
            SceneManager.LoadScene(GameplaySceneName);
        }

        private void OpenApiSettings()
        {
            if (_apiScreen == null)
            {
                var go = new GameObject("ApiSettingsScreen");
                go.transform.SetParent(transform, false);
                _apiScreen = go.AddComponent<ApiSettingsScreen>();
                _apiScreen.Saved += OnApiSaved;
                _apiScreen.Closed += OnApiClosed;
            }
            _apiScreen.Show(ApiConfig.Load());
        }

        private void OnApiSaved(ApiConfig cfg)
        {
            RefreshStatus(null, false);
        }

        private void OnApiClosed()
        {
            if (_overlayVisible) RefreshStatus(null, false);
        }

        private void BackToMenu()
        {
            HideOverlay();
            if (_menu != null) _menu.BackToMain();
        }

        private void RefreshStatus(string extra, bool highlight)
        {
            if (_status == null) return;
            ApiConfig cfg = ApiConfig.Load();
            bool remote = !cfg.offline && cfg.HasKey;
            string line = remote
                ? "远端模型已配置：" + cfg.ResolvedChatModel + " · 四层 Agent 实时生成"
                : "未配置 API Key：将以离线确定性模式生成（也可先点「设置 API Key 与模型」）";
            if (!string.IsNullOrEmpty(extra))
                _status.text = extra + "\n" + line;
            else
                _status.text = line;
            _status.color = highlight
                ? _goldBright
                : (remote ? new Color(0.62f, 0.80f, 0.72f) : new Color(0.66f, 0.62f, 0.52f));
        }

        // ─────────────────────────── UI 构造 ───────────────────────────

        private void BuildUi()
        {
            EnsureEventSystem();

            var canvasGo = new GameObject("TempleStartCanvas", typeof(RectTransform), typeof(Canvas),
                typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            _root = canvasGo;

            var dim = TitleScreen.CreateRect("Dim", _root.transform, new Color(0f, 0f, 0f, 0.40f));
            Stretch(dim);
            dim.GetComponent<Image>().raycastTarget = true;

            var panel = TitleScreen.CreateRect("Panel", _root.transform, _ink);
            var panelRt = panel.GetComponent<RectTransform>();
            panelRt.anchorMin = panelRt.anchorMax = new Vector2(0.5f, 0.5f);
            panelRt.sizeDelta = new Vector2(920f, 720f);
            panelRt.anchoredPosition = Vector2.zero;
            panel.GetComponent<Image>().raycastTarget = true;

            AddFrame(panel.transform, 920f, 720f, _gold, 0.55f);
            AddText(panel.transform, "Caption", "星 火 已 点 燃", 20, new Color(0.72f, 0.62f, 0.44f), new Vector2(0f, 318f), new Vector2(700f, 32f));
            AddText(panel.transform, "Title", "星火史诗 · 开启你的历史", 44, _goldBright, new Vector2(0f, 272f), new Vector2(820f, 60f), FontStyle.Bold, true);
            AddText(panel.transform, "Subtitle", "输入一个历史事件，让四层 Agent 为你实时生成一段可自由探索的 3D 剧情",
                22, new Color(0.72f, 0.70f, 0.63f), new Vector2(0f, 222f), new Vector2(840f, 60f));

            var line = TitleScreen.CreateRect("Divider", panel.transform, new Color(_gold.r, _gold.g, _gold.b, 0.32f));
            SetRect(line, new Vector2(0f, 190f), new Vector2(720f, 2f));

            AddText(panel.transform, "InputLabel", "历史事件", 22, new Color(0.78f, 0.74f, 0.65f),
                new Vector2(-260f, 154f), new Vector2(240f, 40f), FontStyle.Normal, false, TextAnchor.MiddleLeft);

            _input = AddInputField(panel.transform, new Vector2(0f, 104f), new Vector2(760f, 76f));
            _input.onEndEdit.AddListener(delegate (string text)
            {
                if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
                    ConfirmStart();
            });

            AddText(panel.transform, "SuggestLabel", "推荐事件", 20, new Color(0.62f, 0.58f, 0.48f),
                new Vector2(-260f, 46f), new Vector2(240f, 36f), FontStyle.Normal, false, TextAnchor.MiddleLeft);

            System.Collections.Generic.List<string> suggestions = TitleScreen.SuggestEventNames();
            for (int i = 0; i < suggestions.Count && i < 8; i++)
            {
                int column = i % 4;
                int row = i / 4;
                string suggestion = suggestions[i];
                Button chip = TitleScreen.CreateButton("Suggest" + i, panel.transform, suggestion,
                    new Vector2(-288f + column * 192f, 2f - row * 54f), new Vector2(176f, 44f),
                    new Color(0.13f, 0.14f, 0.16f, 1f), new Color(0.24f, 0.20f, 0.13f, 1f));
                PaintButton(chip, new Color(0.78f, 0.70f, 0.54f, 1f), 20);
                chip.onClick.AddListener(delegate
                {
                    if (_input != null)
                    {
                        _input.text = suggestion;
                        _input.ActivateInputField();
                    }
                });
            }

            Button start = TitleScreen.CreateButton("Start", panel.transform, "开始生成",
                new Vector2(-170f, -122f), new Vector2(320f, 72f),
                new Color(0.72f, 0.54f, 0.24f, 1f), new Color(0.90f, 0.72f, 0.36f, 1f));
            PaintButton(start, new Color(0.10f, 0.08f, 0.05f, 1f), 28, FontStyle.Bold);
            start.onClick.AddListener(ConfirmStart);
            _startButton = start;

            Button resume = TitleScreen.CreateButton("Resume", panel.transform, "继续上次存档",
                new Vector2(170f, -122f), new Vector2(320f, 72f),
                new Color(0.12f, 0.15f, 0.18f, 1f), new Color(0.22f, 0.26f, 0.28f, 1f));
            PaintButton(resume, _gold, 24);
            resume.onClick.AddListener(ConfirmResume);
            _resumeButton = resume.gameObject;
            _resumeButton.SetActive(false);

            Button settings = TitleScreen.CreateButton("Settings", panel.transform, "设置 API Key 与模型",
                new Vector2(-180f, -204f), new Vector2(380f, 50f),
                new Color(0.10f, 0.11f, 0.13f, 1f), new Color(0.20f, 0.18f, 0.12f, 1f));
            PaintButton(settings, new Color(0.80f, 0.72f, 0.55f), 22);
            settings.onClick.AddListener(OpenApiSettings);

            Button back = TitleScreen.CreateButton("Back", panel.transform, "返回主菜单",
                new Vector2(250f, -204f), new Vector2(220f, 50f),
                new Color(0.10f, 0.10f, 0.11f, 1f), new Color(0.18f, 0.18f, 0.16f, 1f));
            PaintButton(back, new Color(0.68f, 0.63f, 0.55f), 22);
            back.onClick.AddListener(BackToMenu);

            _status = AddText(panel.transform, "Status", "", 20, new Color(0.66f, 0.62f, 0.52f),
                new Vector2(0f, -262f), new Vector2(840f, 64f));

            AddText(panel.transform, "Hint", "无 Key 也可离线游玩；按 ESC 返回主菜单", 18,
                new Color(0.42f, 0.42f, 0.40f), new Vector2(0f, -312f), new Vector2(840f, 32f));

            _root.SetActive(false);
        }

        private static Text AddText(Transform parent, string name, string content, int size, Color color,
            Vector2 position, Vector2 rectSize, FontStyle style = FontStyle.Normal, bool glow = false,
            TextAnchor align = TextAnchor.MiddleCenter)
        {
            Text text = TitleScreen.CreateText(name, parent, content, size, color);
            text.rectTransform.anchorMin = text.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            text.rectTransform.sizeDelta = rectSize;
            text.rectTransform.anchoredPosition = position;
            text.fontStyle = style;
            text.alignment = align;
            if (glow)
            {
                var shadow = text.gameObject.AddComponent<Shadow>();
                shadow.effectColor = new Color(0.55f, 0.36f, 0.12f, 0.55f);
                shadow.effectDistance = new Vector2(0f, -2f);
                var outline = text.gameObject.AddComponent<Outline>();
                outline.effectColor = new Color(0.22f, 0.13f, 0.04f, 0.85f);
                outline.effectDistance = new Vector2(1.2f, -1.2f);
            }
            return text;
        }

        private static void PaintButton(Button button, Color labelColor, int fontSize, FontStyle style = FontStyle.Normal)
        {
            if (button == null) return;
            Text label = button.GetComponentInChildren<Text>();
            if (label != null)
            {
                label.color = labelColor;
                label.fontSize = fontSize;
                label.fontStyle = style;
            }
        }

        private static void AddFrame(Transform parent, float width, float height, Color color, float alpha)
        {
            Color c = new Color(color.r, color.g, color.b, alpha);
            AddEdge(parent, "FrameTop", new Vector2(0f, height * 0.5f), new Vector2(width, 2f), c);
            AddEdge(parent, "FrameBottom", new Vector2(0f, -height * 0.5f), new Vector2(width, 2f), c);
            AddEdge(parent, "FrameLeft", new Vector2(-width * 0.5f, 0f), new Vector2(2f, height), c);
            AddEdge(parent, "FrameRight", new Vector2(width * 0.5f, 0f), new Vector2(2f, height), c);
        }

        private static void AddEdge(Transform parent, string name, Vector2 position, Vector2 size, Color color)
        {
            var edge = TitleScreen.CreateRect(name, parent, color);
            edge.GetComponent<Image>().raycastTarget = false;
            SetRect(edge, position, size);
        }

        private static InputField AddInputField(Transform parent, Vector2 position, Vector2 size)
        {
            var frame = TitleScreen.CreateRect("InputFrame", parent, new Color(0.84f, 0.67f, 0.36f, 0.45f));
            SetRect(frame, position, new Vector2(size.x + 6f, size.y + 6f));

            var go = new GameObject("EventInput", typeof(RectTransform), typeof(CanvasRenderer),
                typeof(Image), typeof(InputField));
            go.transform.SetParent(parent, false);
            var image = go.GetComponent<Image>();
            image.color = new Color(0.075f, 0.082f, 0.09f, 1f);
            image.raycastTarget = true;
            SetRect(go, position, size);

            Text text = TitleScreen.CreateText("Text", go.transform, "", 30, new Color(0.96f, 0.93f, 0.86f));
            StretchText(text, 22f);
            text.alignment = TextAnchor.MiddleLeft;
            text.supportRichText = false;

            Text placeholder = TitleScreen.CreateText("Placeholder", go.transform, "例如：攻占巴士底狱", 30,
                new Color(0.42f, 0.41f, 0.38f));
            StretchText(placeholder, 22f);
            placeholder.alignment = TextAnchor.MiddleLeft;

            var field = go.GetComponent<InputField>();
            field.textComponent = text;
            field.placeholder = placeholder;
            field.targetGraphic = image;
            field.lineType = InputField.LineType.SingleLine;
            field.characterLimit = 40;
            return field;
        }

        private static void StretchText(Text text, float padding)
        {
            var rt = text.rectTransform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(padding, 6f);
            rt.offsetMax = new Vector2(-padding, -6f);
        }

        private static void Stretch(GameObject go)
        {
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        private static void SetRect(GameObject go, Vector2 position, Vector2 size)
        {
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = size;
            rt.anchoredPosition = position;
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
