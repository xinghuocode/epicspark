// 标题界面：让玩家自由输入一个历史事件（设计文档第四节：游戏允许用户自由输入历史事件）。
// 全部用代码构建 uGUI，不依赖 TMP 与任何预制体；中文字体优先使用系统字体。
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace EpicSpark.Bootstrap
{
    public class TitleScreen : MonoBehaviour
    {
        /// <summary>玩家点击开始后触发，参数为历史事件名称。</summary>
        public static event Action<string> StartRequested;

        /// <summary>玩家点击「继续上次存档」后触发。</summary>
        public static event Action ResumeRequested;

        /// <summary>玩家点击「设置模型端点」后触发。</summary>
        public static event Action SettingsRequested;

        public static Font UiFont { get; private set; }

        private Canvas _canvas;
        private InputField _input;
        private Text _status;
        private GameObject _root;
        private GameObject _resumeButton;

        private static readonly string[] Suggestions =
        {
            "法国大革命",
            "美国独立战争",
            "文艺复兴",
            "秦始皇统一六国",
            "工业革命",
            "攻占巴士底狱",
            "大航海时代",
            "太平天国",
        };

        public static Font GetUiFont()
        {
            if (UiFont != null) return UiFont;
            string[] candidates =
            {
                "Microsoft YaHei", "Microsoft YaHei UI", "SimHei", "SimSun",
                "Noto Sans SC", "Noto Sans CJK SC", "PingFang SC", "Heiti SC",
                "WenQuanYi Micro Hei", "Arial Unicode MS",
            };
            try { UiFont = Font.CreateDynamicFontFromOSFont(candidates, 22); }
            catch (Exception) { UiFont = null; }
            if (UiFont == null)
            {
                try { UiFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); }
                catch (Exception) { }
            }
            if (UiFont == null)
            {
                try { UiFont = Resources.GetBuiltinResource<Font>("Arial.ttf"); }
                catch (Exception) { }
            }
            return UiFont;
        }

        public void Show(string defaultEvent, string statusText)
        {
            BuildIfNeeded();
            if (_input != null) _input.text = defaultEvent ?? "";
            if (_status != null) _status.text = statusText ?? "";
            if (_resumeButton != null) _resumeButton.SetActive(SaveService.HasSave);
            _root.SetActive(true);
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        public void Hide()
        {
            if (_root != null) _root.SetActive(false);
        }

        public void SetStatus(string text)
        {
            if (_status != null) _status.text = text ?? "";
        }

        public bool IsVisible
        {
            get { return _root != null && _root.activeSelf; }
        }

        private void BuildIfNeeded()
        {
            if (_root != null) return;

            EnsureEventSystem();

            var canvasGo = new GameObject("TitleCanvas");
            canvasGo.transform.SetParent(transform, false);
            _canvas = canvasGo.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 100;
            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            canvasGo.AddComponent<GraphicRaycaster>();
            _root = canvasGo;

            // 背景
            var bg = CreateRect("Background", _root.transform, new Color(0.06f, 0.07f, 0.09f, 1f));
            Stretch(bg);

            var accent = CreateRect("AccentLine", _root.transform, new Color(0.16f, 0.82f, 0.74f, 1f));
            var accentRt = accent.GetComponent<RectTransform>();
            accentRt.anchorMin = new Vector2(0.5f, 0.5f);
            accentRt.anchorMax = new Vector2(0.5f, 0.5f);
            accentRt.sizeDelta = new Vector2(760f, 4f);
            accentRt.anchoredPosition = new Vector2(0f, 210f);

            // 标题
            var title = CreateText("Title", _root.transform, "EPICSPARK", 72, new Color(0.94f, 0.95f, 0.96f));
            var titleRt = title.rectTransform;
            titleRt.anchorMin = new Vector2(0.5f, 0.5f);
            titleRt.anchorMax = new Vector2(0.5f, 0.5f);
            titleRt.sizeDelta = new Vector2(1200f, 110f);
            titleRt.anchoredPosition = new Vector2(0f, 300f);
            title.fontStyle = FontStyle.Bold;

            var subtitle = CreateText("Subtitle", _root.transform, "星火史纪 · AI 驱动的历史沉浸体验", 30,
                new Color(0.62f, 0.68f, 0.72f));
            var subRt = subtitle.rectTransform;
            subRt.anchorMin = new Vector2(0.5f, 0.5f);
            subRt.anchorMax = new Vector2(0.5f, 0.5f);
            subRt.sizeDelta = new Vector2(1200f, 60f);
            subRt.anchoredPosition = new Vector2(0f, 250f);

            // 输入提示
            var label = CreateText("InputLabel", _root.transform, "输入一个历史事件", 26, new Color(0.80f, 0.84f, 0.86f));
            var labelRt = label.rectTransform;
            labelRt.anchorMin = new Vector2(0.5f, 0.5f);
            labelRt.anchorMax = new Vector2(0.5f, 0.5f);
            labelRt.sizeDelta = new Vector2(760f, 44f);
            labelRt.anchoredPosition = new Vector2(0f, 150f);
            label.alignment = TextAnchor.MiddleLeft;

            // 输入框
            _input = CreateInputField(_root.transform, new Vector2(0f, 105f), new Vector2(760f, 72f));

            // 建议按钮
            float startX = -570f;
            for (int i = 0; i < Suggestions.Length; i++)
            {
                int column = i % 4;
                int row = i / 4;
                string suggestion = Suggestions[i];
                var button = CreateButton("Suggest" + i, _root.transform, suggestion,
                    new Vector2(startX + 380f + column * 285f, 10f - row * 78f), new Vector2(268f, 62f),
                    new Color(0.14f, 0.17f, 0.20f, 1f), new Color(0.20f, 0.26f, 0.30f, 1f));
                button.onClick.AddListener(delegate
                {
                    if (_input != null) _input.text = suggestion;
                });
            }

            // 开始按钮
            var start = CreateButton("StartButton", _root.transform, "开始游戏",
                new Vector2(-190f, -180f), new Vector2(320f, 84f),
                new Color(0.10f, 0.62f, 0.55f, 1f), new Color(0.14f, 0.78f, 0.68f, 1f));
            start.onClick.AddListener(OnStartClicked);

            // 继续上次存档（无存档时隐藏）
            var resume = CreateButton("ResumeButton", _root.transform, "继续上次存档",
                new Vector2(190f, -180f), new Vector2(320f, 84f),
                new Color(0.18f, 0.24f, 0.32f, 1f), new Color(0.26f, 0.34f, 0.44f, 1f));
            resume.onClick.AddListener(OnResumeClicked);
            _resumeButton = resume.gameObject;
            _resumeButton.SetActive(false);

            // 设置模型端点（API Key / Base URL / 各模型名）
            var settings = CreateButton("SettingsButton", _root.transform, "设置 API Key 与模型",
                new Vector2(0f, -268f), new Vector2(360f, 56f),
                new Color(0.15f, 0.18f, 0.22f, 1f), new Color(0.22f, 0.27f, 0.32f, 1f));
            settings.onClick.AddListener(OnSettingsClicked);

            // 状态行
            _status = CreateText("Status", _root.transform, "", 22, new Color(0.66f, 0.72f, 0.76f));
            var statusRt = _status.rectTransform;
            statusRt.anchorMin = new Vector2(0.5f, 0.5f);
            statusRt.anchorMax = new Vector2(0.5f, 0.5f);
            statusRt.sizeDelta = new Vector2(1400f, 40f);
            statusRt.anchoredPosition = new Vector2(0f, -340f);

            // 说明
            string hint = "无 API Key 时以确定性本地逻辑离线游玩；配置 Key 后由四层 Agent 实时生成场景与剧情。";
            var foot = CreateText("Hint", _root.transform, hint, 20, new Color(0.48f, 0.54f, 0.58f));
            var footRt = foot.rectTransform;
            footRt.anchorMin = new Vector2(0.5f, 0.5f);
            footRt.anchorMax = new Vector2(0.5f, 0.5f);
            footRt.sizeDelta = new Vector2(1400f, 40f);
            footRt.anchoredPosition = new Vector2(0f, -392f);
        }

        private void OnStartClicked()
        {
            string value = _input != null ? _input.text : "";
            if (string.IsNullOrWhiteSpace(value))
            {
                SetStatus("请先输入一个历史事件名称，或点击上方推荐事件。");
                return;
            }
            SetStatus("正在根据「" + value.Trim() + "」检索知识库并生成第一个场景…");
            Action<string> handler = StartRequested;
            if (handler != null) handler(value.Trim());
        }

        public void OnSubmitPressed()
        {
            OnStartClicked();
        }

        private void OnResumeClicked()
        {
            SetStatus("正在读取存档并恢复场景…");
            Action handler = ResumeRequested;
            if (handler != null) handler();
        }

        private void OnSettingsClicked()
        {
            Action handler = SettingsRequested;
            if (handler != null) handler();
        }

        // ─────────────────────────── UI 构造工具 ───────────────────────────

        private static void EnsureEventSystem()
        {
            if (UnityEngine.Object.FindObjectOfType<EventSystem>() != null) return;
            var go = new GameObject("EventSystem");
            go.AddComponent<EventSystem>();
            go.AddComponent<StandaloneInputModule>();
        }

        public static GameObject CreateRect(string name, Transform parent, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(parent, false);
            var image = go.GetComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            return go;
        }

        private static void Stretch(GameObject go)
        {
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        public static Text CreateText(string name, Transform parent, string content, int fontSize, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            go.transform.SetParent(parent, false);
            var text = go.GetComponent<Text>();
            text.font = GetUiFont();
            text.text = content;
            text.fontSize = fontSize;
            text.color = color;
            text.alignment = TextAnchor.MiddleCenter;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(600f, 60f);
            return text;
        }

        public static Button CreateButton(string name, Transform parent, string label, Vector2 position,
            Vector2 size, Color normal, Color highlighted)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            var image = go.GetComponent<Image>();
            image.color = normal;
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = size;
            rt.anchoredPosition = position;

            var button = go.GetComponent<Button>();
            var colors = button.colors;
            colors.normalColor = normal;
            colors.highlightedColor = highlighted;
            colors.pressedColor = new Color(highlighted.r * 0.8f, highlighted.g * 0.8f, highlighted.b * 0.8f, 1f);
            colors.selectedColor = normal;
            button.colors = colors;

            var text = CreateText("Label", go.transform, label, 26, new Color(0.94f, 0.95f, 0.96f));
            var textRt = text.rectTransform;
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.offsetMin = new Vector2(8f, 4f);
            textRt.offsetMax = new Vector2(-8f, -4f);
            return button;
        }

        private InputField CreateInputField(Transform parent, Vector2 position, Vector2 size)
        {
            var go = new GameObject("EventInput", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(InputField));
            go.transform.SetParent(parent, false);
            var image = go.GetComponent<Image>();
            image.color = new Color(0.12f, 0.14f, 0.16f, 1f);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = size;
            rt.anchoredPosition = position;

            var text = CreateText("Text", go.transform, "", 30, new Color(0.95f, 0.96f, 0.97f));
            var textRt = text.rectTransform;
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.offsetMin = new Vector2(20f, 6f);
            textRt.offsetMax = new Vector2(-20f, -6f);
            text.alignment = TextAnchor.MiddleLeft;
            text.supportRichText = false;

            var placeholder = CreateText("Placeholder", go.transform, "例如：法国大革命", 30, new Color(0.42f, 0.46f, 0.50f));
            var phRt = placeholder.rectTransform;
            phRt.anchorMin = Vector2.zero;
            phRt.anchorMax = Vector2.one;
            phRt.offsetMin = new Vector2(20f, 6f);
            phRt.offsetMax = new Vector2(-20f, -6f);
            placeholder.alignment = TextAnchor.MiddleLeft;

            var field = go.GetComponent<InputField>();
            field.textComponent = text;
            field.placeholder = placeholder;
            field.targetGraphic = image;
            field.lineType = InputField.LineType.SingleLine;
            field.characterLimit = 40;
            return field;
        }

        public static List<string> SuggestEventNames()
        {
            return new List<string>(Suggestions);
        }
    }
}
