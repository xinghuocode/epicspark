// 星火神殿开始场景桥接：把 BlenderStarTemple_Gameplay（第一人称神殿 + 世界空间菜单）
// 接到 EpicSpark 主流程。所有开始流程都长在神殿自带的「漂浮对话窗」里，不额外弹独立大面板：
//
//   漂浮对话窗 →「进入史诗…」→ 创建新史诗 / 加载已有史诗
//       创建新史诗 → 窗内新建页：输入历史事件 →「开始生成」→ 载入 EpicSpark 主场景
//       加载已有史诗 → 读取上次存档 → 载入 EpicSpark 主场景
//   漂浮对话窗 →「设置」→ 窗内设置页：API Key 与模型 / 生成容忍度滑块
//
// 该组件由 RuntimeInitializeOnLoadMethod 自动挂到含 StarTempleWorldUiController 的场景里，
// 不需要在场景里额外摆对象；在 EpicSpark 主场景里不会生效。
using System;
using System.Collections.Generic;
using System.IO;
using EpicSpark.Core;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace EpicSpark.Bootstrap
{
    /// <summary>星火神殿开始场景 → 创建/加载史诗 → EpicSpark 主场景。</summary>
    public class StarTempleStartBridge : MonoBehaviour
    {
        /// <summary>开局场景名（必须出现在 Build Settings 里）。</summary>
        public const string GameplaySceneName = "EpicSpark";

        private static readonly Color PanelBg = new Color(0.02f, 0.03f, 0.05f, 0.985f);
        private static readonly Color ButtonNormal = new Color(0.06f, 0.08f, 0.11f, 0.98f);
        private static readonly Color ButtonHover = new Color(0.28f, 0.20f, 0.08f, 1f);
        private static readonly Color Gold = new Color(0.94f, 0.68f, 0.25f, 1f);
        private static readonly Color TextMain = new Color(0.97f, 0.93f, 0.82f, 1f);
        private static readonly Color TextDim = new Color(0.67f, 0.68f, 0.64f, 1f);

        private SparkCode.Gameplay.StarTempleWorldUiController _worldUi;
        private SparkCode.Gameplay.StarTemplePlayerController _player;
        private ApiSettingsScreen _apiScreen;

        private GameObject _createPage;
        private InputField _createInput;
        private Text _createStatus;
        private Text _loadHint;
        private GameObject _epicPageBeforeCreate;
        private GameObject _globalBackButton;
        private string _bridgeTitleBeforeCreate;

        private Slider _toleranceSlider;
        private Text _toleranceValue;

        private bool _frozenWorld;
        private bool _apiOpen;

        /// <summary>新建史诗页是否打开（自动化测试用）。</summary>
        public bool IsCreatePageOpen
        {
            get { return _createPage != null && _createPage.activeSelf; }
        }

        /// <summary>设置页的生成容忍度（自动化测试用）。</summary>
        public float ToleranceValue
        {
            get
            {
                return _toleranceSlider != null
                    ? _toleranceSlider.value
                    : PlayerPrefs.GetFloat(GameBootstrap.BootToleranceKey, 0.5f);
            }
        }

        /// <summary>自动化测试用：直接设置容忍度滑块。</summary>
        public void SetToleranceForTest(float value)
        {
            if (_toleranceSlider != null) _toleranceSlider.value = Mathf.Clamp01(value);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoCreate()
        {
            if (UnityEngine.Object.FindObjectOfType<StarTempleStartBridge>() != null) return;
            if (UnityEngine.Object.FindObjectOfType<SparkCode.Gameplay.StarTempleWorldUiController>() == null) return;
            var go = new GameObject("StarTempleStartBridge");
            go.AddComponent<StarTempleStartBridge>();
            Debug.Log("[神殿] 已挂载星火神殿开始场景桥接。");
        }

        private void Awake()
        {
            Bind();
            BuildWorldUi();
        }

        private void OnDestroy()
        {
            Unbind();
            if (_apiScreen != null)
            {
                _apiScreen.Closed -= OnApiClosed;
                _apiScreen = null;
            }
        }

        private void Bind()
        {
            _worldUi = UnityEngine.Object.FindObjectOfType<SparkCode.Gameplay.StarTempleWorldUiController>();
            _player = UnityEngine.Object.FindObjectOfType<SparkCode.Gameplay.StarTemplePlayerController>();
            if (_worldUi == null) return;
            _worldUi.CreateEpicRequested -= OnCreateEpic;
            _worldUi.CreateEpicRequested += OnCreateEpic;
            _worldUi.LoadEpicRequested -= OnLoadEpic;
            _worldUi.LoadEpicRequested += OnLoadEpic;
            _worldUi.ApiSettingsRequested -= OnApiSettings;
            _worldUi.ApiSettingsRequested += OnApiSettings;
        }

        private void Unbind()
        {
            if (_worldUi == null) return;
            _worldUi.CreateEpicRequested -= OnCreateEpic;
            _worldUi.LoadEpicRequested -= OnLoadEpic;
            _worldUi.ApiSettingsRequested -= OnApiSettings;
        }

        private void Update()
        {
            if (_worldUi == null)
            {
                Bind();
                if (_worldUi == null) return;
            }

            if (!Input.GetKeyDown(KeyCode.Escape)) return;
            if (_apiScreen != null && _apiScreen.IsVisible)
            {
                _apiScreen.Hide();
                return;
            }
            if (IsCreatePageOpen) CloseCreatePage();
        }

        // ─────────────────────────── 页面入口 ───────────────────────────

        private void OnCreateEpic()
        {
            OpenCreatePage();
        }

        private void OnLoadEpic()
        {
            if (!SaveService.HasSave)
            {
                ShowLoadHint("暂无存档，请先创建新史诗");
                return;
            }
            ConfirmResume();
        }

        private void OnApiSettings()
        {
            EnsureEventSystem();
            if (_apiScreen == null)
            {
                var go = new GameObject("ApiSettingsScreen");
                go.transform.SetParent(transform, false);
                _apiScreen = go.AddComponent<ApiSettingsScreen>();
                _apiScreen.Closed += OnApiClosed;
            }
            _apiScreen.Show(ApiConfig.Load());
            SetFrozen(true);
            _apiOpen = true;
        }

        private void OnApiClosed()
        {
            _apiOpen = false;
            SetFrozen(false);
        }

        // ─────────────────────────── 新建史诗页 ───────────────────────────

        private void OpenCreatePage()
        {
            if (_createPage == null) return;
            if (_worldUi != null && _worldUi.epicPage != null)
            {
                _epicPageBeforeCreate = _worldUi.epicPage;
                _epicPageBeforeCreate.SetActive(false);
            }
            if (_worldUi != null && _worldUi.bridgeTitle != null)
            {
                _bridgeTitleBeforeCreate = _worldUi.bridgeTitle.text;
                _worldUi.bridgeTitle.text = "星火史诗";
            }
            if (_globalBackButton == null && _worldUi != null && _worldUi.bridgeCanvas != null)
            {
                Transform back = FindDeep(_worldUi.bridgeCanvas.transform, "Back_Button");
                if (back != null) _globalBackButton = back.gameObject;
            }
            if (_globalBackButton != null) _globalBackButton.SetActive(false);

            _createPage.SetActive(true);
            SetStatus("", false);
            if (_createInput != null)
            {
                _createInput.text = "";
                if (EventSystem.current != null)
                    EventSystem.current.SetSelectedGameObject(_createInput.gameObject);
                _createInput.ActivateInputField();
            }
            SetFrozen(true);
        }

        private void CloseCreatePage()
        {
            if (_createPage != null) _createPage.SetActive(false);
            if (_epicPageBeforeCreate != null) _epicPageBeforeCreate.SetActive(true);
            _epicPageBeforeCreate = null;
            if (_globalBackButton != null) _globalBackButton.SetActive(true);
            if (_worldUi != null && _worldUi.bridgeTitle != null)
                _worldUi.bridgeTitle.text = string.IsNullOrEmpty(_bridgeTitleBeforeCreate)
                    ? "进入史诗..." : _bridgeTitleBeforeCreate;
            if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);
            SetFrozen(false);
        }

        /// <summary>自动化测试用：直接打开新建页。</summary>
        public void OpenCreatePageForTest()
        {
            OpenCreatePage();
        }

        /// <summary>自动化测试用：填入事件并直接开局。</summary>
        public void SubmitEvent(string value)
        {
            if (_createInput == null) return;
            _createInput.text = value;
            ConfirmStart();
        }

        private void ConfirmStart()
        {
            string value = _createInput != null ? (_createInput.text ?? "").Trim() : "";
            if (value.Length == 0)
            {
                SetStatus("请先输入一个历史事件名称，或点击上方推荐事件。", true);
                if (_createInput != null) _createInput.ActivateInputField();
                return;
            }
            if (value.Length > 40) value = value.Substring(0, 40);
            SetStatus("星火已点燃，正在载入「" + value + "」…", true);
            PlayerPrefs.SetString(GameBootstrap.BootEventKey, value);
            PlayerPrefs.DeleteKey(GameBootstrap.BootResumeKey);
            PlayerPrefs.Save();
            SceneManager.LoadScene(GameplaySceneName);
        }

        private void ConfirmResume()
        {
            PlayerPrefs.SetInt(GameBootstrap.BootResumeKey, 1);
            PlayerPrefs.DeleteKey(GameBootstrap.BootEventKey);
            PlayerPrefs.Save();
            SceneManager.LoadScene(GameplaySceneName);
        }

        private void SetFrozen(bool frozen)
        {
            if (_frozenWorld == frozen) return;
            _frozenWorld = frozen;
            if (_worldUi != null) _worldUi.SuspendInteraction = frozen;
            if (_player != null)
            {
                _player.SetModalFrozen(frozen);
                _player.HudHidden = frozen;
            }
            if (frozen)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
        }

        // ─────────────────────────── 设置页 ───────────────────────────

        private void OnToleranceChanged(float value)
        {
            PlayerPrefs.SetFloat(GameBootstrap.BootToleranceKey, value);
            PlayerPrefs.Save();
            RefreshToleranceLabel(value);
        }

        private void RefreshToleranceLabel(float value)
        {
            if (_toleranceValue != null) _toleranceValue.text = value.ToString("0.00");
        }

        private void ShowLoadHint(string message)
        {
            if (_loadHint == null) return;
            _loadHint.text = message;
            _loadHint.gameObject.SetActive(true);
        }

        // ─────────────────────────── 世界 UI 构建 ───────────────────────────

        private void BuildWorldUi()
        {
            if (_worldUi == null || _worldUi.bridgeCanvas == null) return;
            EnsureEventSystem();
            EnsureRaycaster();

            BuildEpicPageEntries();
            BuildCreatePage();
            BuildSettingsPage();
            BuildLoadHint();
        }

        private void EnsureRaycaster()
        {
            if (_worldUi.bridgeCanvas.GetComponent<GraphicRaycaster>() == null)
                _worldUi.bridgeCanvas.AddComponent<GraphicRaycaster>();
        }

        private static void EnsureEventSystem()
        {
            if (UnityEngine.Object.FindObjectOfType<EventSystem>() != null) return;
            var go = new GameObject("EventSystem");
            go.AddComponent<EventSystem>();
            go.AddComponent<StandaloneInputModule>();
        }

        private static Transform FindDeep(Transform root, string name)
        {
            if (root == null) return null;
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var found = FindDeep(root.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }

        private static void RegisterButton(SparkCode.Gameplay.StarTempleWorldUiController worldUi,
            GameObject go, SparkCode.Gameplay.StarTempleUiAction action, Color normal, Color hover)
        {
            if (go == null) return;
            var image = go.GetComponent<Image>();
            var button = go.GetComponent<SparkCode.Gameplay.StarTempleWorldUiButton>();
            if (button == null) button = go.AddComponent<SparkCode.Gameplay.StarTempleWorldUiButton>();
            button.action = action;
            button.background = image;
            button.label = FindDeep(go.transform, "Column_Title") != null
                ? FindDeep(go.transform, "Column_Title").GetComponent<Text>()
                : null;
            button.normalColor = normal;
            button.hoverColor = hover;
            if (image != null) image.color = normal;
            if (worldUi.buttons == null) worldUi.buttons = new List<SparkCode.Gameplay.StarTempleWorldUiButton>();
            if (!worldUi.buttons.Contains(button)) worldUi.buttons.Add(button);
        }

        private void BuildEpicPageEntries()
        {
            if (_worldUi.epicPage == null) return;
            Transform createColumn = FindDeep(_worldUi.epicPage.transform, "Create_Epic_Column");
            Transform loadColumn = FindDeep(_worldUi.epicPage.transform, "Load_Epic_Column");

            RegisterButton(_worldUi, createColumn != null ? createColumn.gameObject : null,
                SparkCode.Gameplay.StarTempleUiAction.CreateEpic, ButtonNormal, ButtonHover);
            RegisterButton(_worldUi, loadColumn != null ? loadColumn.gameObject : null,
                SparkCode.Gameplay.StarTempleUiAction.LoadEpic, ButtonNormal, ButtonHover);

            if (loadColumn != null)
            {
                // 让「加载已有史诗」这一列可直接点击；上方标题保持原样。
                var hint = MakeText(loadColumn, "EpicSpark_LoadState", "", 20, TextDim,
                    new Vector2(0f, -112f), new Vector2(340f, 70f), TextAnchor.MiddleCenter);
                hint.horizontalOverflow = HorizontalWrapMode.Wrap;
                hint.gameObject.SetActive(false);
            }
        }

        private void BuildLoadHint()
        {
            Transform loadColumn = _worldUi.epicPage != null
                ? FindDeep(_worldUi.epicPage.transform, "Load_Epic_Column") : null;
            Transform loadHint = loadColumn != null ? FindDeep(loadColumn, "EpicSpark_LoadState") : null;
            _loadHint = loadHint != null ? loadHint.GetComponent<Text>() : null;
            RefreshLoadHint();
        }

        private void RefreshLoadHint()
        {
            if (_loadHint == null) return;
            if (!SaveService.HasSave)
            {
                _loadHint.text = "暂无存档\n请先创建新史诗";
                _loadHint.gameObject.SetActive(true);
                return;
            }

            GameSaveData data = null;
            try { data = SaveService.Load(); } catch (Exception) { }
            if (data == null)
            {
                _loadHint.text = "存档读取失败\n请创建新史诗";
                _loadHint.gameObject.SetActive(true);
                return;
            }

            string when = "";
            try { when = File.GetLastWriteTime(SaveService.SavePath).ToString("MM-dd HH:mm"); } catch (Exception) { }
            string name = string.IsNullOrEmpty(data.userEvent) ? "未命名事件" : data.userEvent;
            _loadHint.text = "上次存档：" + name + (string.IsNullOrEmpty(when) ? "" : "\n" + when);
            _loadHint.gameObject.SetActive(true);
        }

        private void BuildCreatePage()
        {
            var parent = _worldUi.bridgeCanvas.transform;
            _createPage = MakePanel(parent, "EpicSpark_CreatePage", new Vector2(0f, -34f),
                new Vector2(850f, 400f), PanelBg);

            MakeText(_createPage.transform, "Title", "创建新史诗", 40, TextMain,
                new Vector2(0f, 160f), new Vector2(700f, 56f), TextAnchor.MiddleCenter);
            MakeRule(_createPage.transform, new Vector2(0f, 128f), new Vector2(700f, 2f));

            MakePanel(_createPage.transform, "InputFrame", new Vector2(0f, 60f),
                new Vector2(748f, 88f), new Color(0.94f, 0.68f, 0.25f, 0.55f));
            _createInput = MakeInputField(_createPage.transform, new Vector2(0f, 60f), new Vector2(736f, 76f));
            _createInput.onEndEdit.AddListener(delegate (string text)
            {
                if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
                    ConfirmStart();
            });

            List<string> suggestions = TitleScreen.SuggestEventNames();
            for (int i = 0; i < suggestions.Count && i < 8; i++)
            {
                int column = i % 4;
                int row = i / 4;
                string suggestion = suggestions[i];
                MakeButton(_createPage.transform, "Suggest" + i, suggestion,
                    new Vector2(-270f + column * 180f, 6f - row * 52f), new Vector2(164f, 44f),
                    new Color(0.10f, 0.12f, 0.15f, 1f), new Color(0.24f, 0.20f, 0.13f, 1f),
                    delegate
                    {
                        if (_createInput != null)
                        {
                            _createInput.text = suggestion;
                            _createInput.ActivateInputField();
                        }
                    }, 22);
            }

            MakeButton(_createPage.transform, "Start", "开始生成",
                new Vector2(-170f, -104f), new Vector2(300f, 66f),
                new Color(0.72f, 0.54f, 0.24f, 1f), new Color(0.90f, 0.72f, 0.36f, 1f),
                ConfirmStart, 30, new Color(0.10f, 0.08f, 0.05f, 1f));
            MakeButton(_createPage.transform, "Back", "返回上一级",
                new Vector2(170f, -104f), new Vector2(220f, 66f),
                ButtonNormal, ButtonHover, CloseCreatePage, 24);

            _createStatus = MakeText(_createPage.transform, "Status", "", 20, TextDim,
                new Vector2(0f, -158f), new Vector2(760f, 30f), TextAnchor.MiddleCenter);

            MakeText(_createPage.transform, "Hint",
                "输入历史事件，或点推荐事件；无 Key 也能离线游玩，ESC 返回",
                18, new Color(0.48f, 0.47f, 0.43f), new Vector2(0f, -190f), new Vector2(820f, 28f),
                TextAnchor.MiddleCenter);

            _createPage.SetActive(false);
        }

        private void BuildSettingsPage()
        {
            Transform page = _worldUi.settingsPage != null
                ? _worldUi.settingsPage.transform : null;
            if (page == null) return;
            Transform frame = FindDeep(page, "Settings_Empty_Frame");
            Transform parent = frame != null ? frame : page;

            GameObject apiButton = MakePanel(parent, "EpicSpark_ApiButton",
                new Vector2(0f, 112f), new Vector2(560f, 76f), ButtonNormal);
            AddOutlines(apiButton.transform, 560f, 0.58f);
            MakeText(apiButton.transform, "Label", "设置 API Key 与模型", 28, TextMain,
                Vector2.zero, new Vector2(540f, 64f), TextAnchor.MiddleCenter);
            RegisterButton(_worldUi, apiButton, SparkCode.Gameplay.StarTempleUiAction.OpenApi,
                ButtonNormal, ButtonHover);

            MakeRule(parent, new Vector2(0f, 58f), new Vector2(600f, 1f));

            MakeText(parent, "ToleranceLabel", "资产容忍度（素模不生效）", 24, TextMain,
                new Vector2(-210f, 18f), new Vector2(220f, 36f), TextAnchor.MiddleLeft);
            _toleranceValue = MakeText(parent, "ToleranceValue", "0.50", 24, Gold,
                new Vector2(215f, 18f), new Vector2(150f, 36f), TextAnchor.MiddleRight);

            _toleranceSlider = MakeSlider(parent, new Vector2(0f, -36f), new Vector2(560f, 30f));
            float tolerance = PlayerPrefs.GetFloat(GameBootstrap.BootToleranceKey, 0.5f);
            _toleranceSlider.SetValueWithoutNotify(Mathf.Clamp01(tolerance));
            RefreshToleranceLabel(_toleranceSlider.value);
            _toleranceSlider.onValueChanged.AddListener(OnToleranceChanged);

            MakeText(parent, "ToleranceHint",
                "当前版本强制简单几何体；此历史资产设置仅保留兼容测试", 
                18, new Color(0.48f, 0.47f, 0.43f), new Vector2(0f, -96f), new Vector2(680f, 28f),
                TextAnchor.MiddleCenter);
        }

        // ─────────────────────────── UGUI 工具 ───────────────────────────

        private static GameObject MakePanel(Transform parent, string name, Vector2 position, Vector2 size, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = size;
            rt.anchoredPosition = position;
            var image = go.GetComponent<Image>();
            image.color = color;
            image.raycastTarget = true;
            return go;
        }

        private static Text MakeText(Transform parent, string name, string content, int size, Color color,
            Vector2 position, Vector2 rectSize, TextAnchor alignment)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = rectSize;
            rt.anchoredPosition = position;
            var text = go.GetComponent<Text>();
            text.text = content;
            text.font = GetFont();
            text.fontSize = size;
            text.color = color;
            text.alignment = alignment;
            text.raycastTarget = false;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            return text;
        }

        private static void MakeRule(Transform parent, Vector2 position, Vector2 size)
        {
            var go = MakePanel(parent, "Rule", position, size, new Color(0.72f, 0.52f, 0.23f, 0.80f));
            go.GetComponent<Image>().raycastTarget = false;
        }

        private static void AddOutlines(Transform parent, float width, float alpha)
        {
            var color = new Color(0.58f, 0.43f, 0.19f, alpha);
            MakeEdge(parent, "Outline_Top", new Vector2(0f, 1f), new Vector2(width, 2f), color);
            MakeEdge(parent, "Outline_Bottom", new Vector2(0f, 0f), new Vector2(width, 2f), color);
        }

        private static void MakeEdge(Transform parent, string name, Vector2 anchor, Vector2 size, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = anchor;
            rt.sizeDelta = size;
            rt.anchoredPosition = Vector2.zero;
            var image = go.GetComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
        }

        private static Button MakeButton(Transform parent, string name, string label,
            Vector2 position, Vector2 size, Color normal, Color hover, UnityEngine.Events.UnityAction onClick,
            int fontSize, Color? labelColor = null)
        {
            var go = MakePanel(parent, name, position, size, normal);
            var image = go.GetComponent<Image>();
            image.raycastTarget = true;
            var button = go.AddComponent<Button>();
            button.targetGraphic = image;
            var colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.25f, 1.25f, 1.25f, 1f);
            colors.pressedColor = new Color(0.85f, 0.85f, 0.85f, 1f);
            button.colors = colors;
            MakeText(go.transform, "Label", label, fontSize, labelColor ?? TextMain,
                Vector2.zero, new Vector2(size.x - 16f, size.y - 8f), TextAnchor.MiddleCenter);
            AddOutlines(go.transform, size.x, 0.60f);
            if (onClick != null) button.onClick.AddListener(onClick);
            return button;
        }

        private static InputField MakeInputField(Transform parent, Vector2 position, Vector2 size)
        {
            var go = new GameObject("Input", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image),
                typeof(InputField));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = size;
            rt.anchoredPosition = position;
            var image = go.GetComponent<Image>();
            image.color = new Color(0.075f, 0.082f, 0.09f, 1f);
            image.raycastTarget = true;

            Text text = MakeText(go.transform, "Text", "", 30, new Color(0.96f, 0.93f, 0.86f),
                Vector2.zero, size, TextAnchor.MiddleLeft);
            StretchText(text, 18f);
            text.supportRichText = false;

            Text placeholder = MakeText(go.transform, "Placeholder", "例如：攻占巴士底狱", 30,
                new Color(0.42f, 0.41f, 0.38f), Vector2.zero, size, TextAnchor.MiddleLeft);
            StretchText(placeholder, 18f);

            var field = go.GetComponent<InputField>();
            field.textComponent = text;
            field.placeholder = placeholder;
            field.targetGraphic = image;
            field.lineType = InputField.LineType.SingleLine;
            field.characterLimit = 40;
            field.caretColor = Gold;
            field.selectionColor = new Color(0.94f, 0.68f, 0.25f, 0.35f);
            return field;
        }

        private static Slider MakeSlider(Transform parent, Vector2 position, Vector2 size)
        {
            var go = new GameObject("ToleranceSlider", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var rootImage = go.GetComponent<Image>();
            rootImage.color = new Color(0f, 0f, 0f, 0f);
            rootImage.raycastTarget = true;
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = size;
            rt.anchoredPosition = position;

            var background = MakePanel(go.transform, "Background", Vector2.zero, new Vector2(size.x, 10f),
                new Color(0.13f, 0.13f, 0.15f, 1f));
            background.GetComponent<Image>().raycastTarget = false;

            var fillArea = new GameObject("Fill Area", typeof(RectTransform));
            fillArea.transform.SetParent(go.transform, false);
            var fillAreaRt = (RectTransform)fillArea.transform;
            fillAreaRt.anchorMin = new Vector2(0f, 0.5f);
            fillAreaRt.anchorMax = new Vector2(1f, 0.5f);
            fillAreaRt.sizeDelta = new Vector2(-24f, 10f);
            fillAreaRt.anchoredPosition = Vector2.zero;

            var fill = MakePanel(fillArea.transform, "Fill", Vector2.zero, new Vector2(10f, 10f),
                new Color(0.80f, 0.58f, 0.22f, 1f));
            fill.GetComponent<Image>().raycastTarget = false;
            var fillRt = (RectTransform)fill.transform;
            fillRt.anchorMin = new Vector2(0f, 0f);
            fillRt.anchorMax = new Vector2(1f, 1f);
            fillRt.sizeDelta = Vector2.zero;
            fillRt.anchoredPosition = Vector2.zero;

            var handleArea = new GameObject("Handle Slide Area", typeof(RectTransform));
            handleArea.transform.SetParent(go.transform, false);
            var handleAreaRt = (RectTransform)handleArea.transform;
            handleAreaRt.anchorMin = new Vector2(0f, 0f);
            handleAreaRt.anchorMax = new Vector2(1f, 1f);
            handleAreaRt.sizeDelta = new Vector2(-24f, 0f);
            handleAreaRt.anchoredPosition = Vector2.zero;

            var handle = MakePanel(handleArea.transform, "Handle", Vector2.zero, new Vector2(26f, 26f),
                new Color(0.97f, 0.93f, 0.82f, 1f));
            var handleRt = (RectTransform)handle.transform;
            handleRt.anchorMin = new Vector2(0.5f, 0.5f);
            handleRt.anchorMax = new Vector2(0.5f, 0.5f);
            handleRt.sizeDelta = new Vector2(26f, 26f);
            handleRt.anchoredPosition = Vector2.zero;

            var slider = go.AddComponent<Slider>();
            slider.fillRect = fillRt;
            slider.handleRect = handleRt;
            slider.targetGraphic = handle.GetComponent<Image>();
            slider.direction = Slider.Direction.LeftToRight;
            slider.minValue = 0f;
            slider.maxValue = 1f;
            slider.wholeNumbers = false;
            return slider;
        }

        private static Font GetFont()
        {
            var worldUi = UnityEngine.Object.FindObjectOfType<SparkCode.Gameplay.StarTempleWorldUiController>();
            if (worldUi != null && worldUi.uiFont != null) return worldUi.uiFont;
            return Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        }

        private static void StretchText(Text text, float padding)
        {
            var rt = text.rectTransform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(padding, 6f);
            rt.offsetMax = new Vector2(-padding, -6f);
        }

        private void SetStatus(string message, bool highlight)
        {
            if (_createStatus == null) return;
            _createStatus.text = message ?? "";
            _createStatus.color = highlight ? Gold : TextDim;
        }
    }
}
