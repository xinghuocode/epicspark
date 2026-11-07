// 容忍度滑块（设计文档第五节：滑块值 0..1，1 = 容忍度为 0，最严格）。
// 仅在标题界面或按住 Tab 时显示。
using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using EpicSpark.Tools;

namespace EpicSpark.UI
{
    /// <summary>容忍度滑块。数值变化通过 static ValueChanged 通知上层。</summary>
    public class ToleranceSliderUi : MonoBehaviour
    {
        /// <summary>滑块值变化广播（0..1）。</summary>
        public static Action<float> ValueChanged;

        public static ToleranceSliderUi Instance { get; private set; }

        [Header("取值")]
        [SerializeField] private float _value = 0.5f;
        public float minValue = 0f;
        public float maxValue = 1f;

        [Header("显示")]
        [Tooltip("允许在游玩中按住 Tab 显示")]
        public bool allowTabToggle = true;
        public KeyCode toggleKey = KeyCode.Tab;
        public string title = "容忍度";

        public float Value
        {
            get { return _value; }
            set { SetValueSilent(value); }
        }

        /// <summary>是否正在显示。</summary>
        public bool Visible { get; private set; }

        /// <summary>标题界面模式（强制显示）。</summary>
        public bool TitleMode { get; private set; }

        private Canvas _canvas;
        private CanvasGroup _group;
        private Slider _slider;
        private Text _valueText;
        private bool _built;
        private bool _suppress;
        private bool _cursorGrabbed;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.Log("[EpicSpark] 已存在 ToleranceSliderUi，销毁重复实例。");
                Destroy(gameObject);
                return;
            }
            Instance = this;
            EnsureEventSystem();
            Build();
            SetValueSilent(_value);
            ApplyVisibility(false);
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            bool show = TitleMode || (allowTabToggle && Input.GetKey(toggleKey));
            ApplyVisibility(show);
        }

        /// <summary>HudController 用的工厂：在给定父节点下创建滑块（自带 Canvas）。</summary>
        public static ToleranceSliderUi Create(Transform parent)
        {
            GameObject go = new GameObject("ToleranceSliderUi");
            if (parent != null) go.transform.SetParent(parent, false);
            return go.AddComponent<ToleranceSliderUi>();
        }

        /// <summary>标题界面用：强制显示/隐藏滑块。</summary>
        public void SetVisible(bool visible)
        {
            TitleMode = visible;
            ApplyVisibility(visible || (allowTabToggle && Input.GetKey(toggleKey)));
        }

        /// <summary>静默设置值（不触发 ValueChanged）。</summary>
        public void SetValueSilent(float value)
        {
            _value = Mathf.Clamp(value, minValue, maxValue);
            _suppress = true;
            if (_slider != null) _slider.value = _value;
            _suppress = false;
            UpdateValueText();
        }

        // ───────────────────────── 构建 ─────────────────────────

        private void Build()
        {
            if (_built) return;
            _built = true;

            _canvas = UiFactory.CreateCanvas("ToleranceCanvas", 50, true);
            _canvas.transform.SetParent(transform, false);

            _group = _canvas.gameObject.AddComponent<CanvasGroup>();
            _group.alpha = 0f;
            _group.blocksRaycasts = false;
            _group.interactable = false;

            RectTransform panel = UiFactory.NewRect("Panel", _canvas.transform);
            UiFactory.SetAnchors(panel, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                new Vector2(0f, 96f), new Vector2(760f, 96f));

            Image background = UiFactory.NewImage("Background", panel, new Color(0f, 0f, 0f, 0.55f));
            UiFactory.Stretch(background.rectTransform, 0f, 0f, 0f, 0f);
            background.raycastTarget = true;

            Text label = UiFactory.NewText("Title", panel, 22, new Color(1f, 1f, 1f, 0.92f), TextAnchor.MiddleLeft);
            label.text = title;
            UiFactory.SetAnchors(label.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                new Vector2(20f, 0f), new Vector2(200f, 34f));

            RectTransform sliderRoot = UiFactory.NewRect("Slider", panel);
            UiFactory.SetAnchors(sliderRoot, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(-30f, 0f), new Vector2(380f, 26f));
            BuildSlider(sliderRoot);

            _valueText = UiFactory.NewText("Value", panel, 22, new Color(0.55f, 0.9f, 1f, 1f), TextAnchor.MiddleRight);
            UiFactory.SetAnchors(_valueText.rectTransform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                new Vector2(-20f, 0f), new Vector2(110f, 34f));
            UpdateValueText();
        }

        private void BuildSlider(RectTransform parent)
        {
            Slider slider = parent.gameObject.AddComponent<Slider>();

            Image background = UiFactory.NewImage("Background", parent, new Color(1f, 1f, 1f, 0.16f));
            UiFactory.Stretch(background.rectTransform, 6f, 8f, 6f, 8f);
            background.raycastTarget = true;

            RectTransform fillArea = UiFactory.NewRect("Fill Area", parent);
            UiFactory.Stretch(fillArea, 6f, 8f, 6f, 8f);

            Image fill = UiFactory.NewImage("Fill", fillArea, new Color(0.35f, 0.85f, 1f, 0.9f));
            RectTransform fillRect = fill.rectTransform;
            fillRect.anchorMin = new Vector2(0f, 0f);
            fillRect.anchorMax = new Vector2(1f, 1f);
            fillRect.offsetMin = Vector2.zero;
            fillRect.offsetMax = Vector2.zero;

            RectTransform handleArea = UiFactory.NewRect("Handle Slide Area", parent);
            UiFactory.Stretch(handleArea, 0f, 0f, 0f, 0f);

            Image handle = UiFactory.NewImage("Handle", handleArea, new Color(1f, 1f, 1f, 0.95f));
            handle.raycastTarget = true;
            RectTransform handleRect = handle.rectTransform;
            handleRect.anchorMin = new Vector2(0f, 0f);
            handleRect.anchorMax = new Vector2(0f, 1f);
            handleRect.sizeDelta = new Vector2(20f, 0f);

            slider.fillRect = fillRect;
            slider.handleRect = handleRect;
            slider.targetGraphic = handle;
            slider.direction = Slider.Direction.LeftToRight;
            slider.minValue = minValue;
            slider.maxValue = maxValue;
            slider.wholeNumbers = false;
            slider.value = _value;
            slider.onValueChanged.AddListener(HandleSliderChanged);

            _slider = slider;
        }

        private void UpdateValueText()
        {
            if (_valueText != null) _valueText.text = _value.ToString("0.00");
        }

        private void HandleSliderChanged(float value)
        {
            _value = Mathf.Clamp(value, minValue, maxValue);
            UpdateValueText();
            if (_suppress) return;
            Action<float> handler = ValueChanged;
            if (handler != null) handler(_value);
        }

        // ───────────────────────── 显隐与鼠标 ─────────────────────────

        private void ApplyVisibility(bool show)
        {
            if (!_built) return;
            if (Visible == show) return;
            Visible = show;

            if (_canvas != null) _canvas.enabled = show;
            if (_group != null)
            {
                _group.alpha = show ? 1f : 0f;
                _group.blocksRaycasts = show;
                _group.interactable = show;
            }

            if (show) GrabCursor();
            else ReleaseCursor();
        }

        private void GrabCursor()
        {
            EpicSpark.Player.EpicSparkPlayerController player = RuntimeContext.Player;
            if (player == null) return;
            if (!player.IsCursorLocked) return;
            _cursorGrabbed = true;
            player.SetCursorLocked(false);
        }

        private void ReleaseCursor()
        {
            if (!_cursorGrabbed) return;
            _cursorGrabbed = false;
            EpicSpark.Player.EpicSparkPlayerController player = RuntimeContext.Player;
            if (player != null) player.SetCursorLocked(true);
        }

        private static void EnsureEventSystem()
        {
            if (UnityEngine.Object.FindObjectOfType<EventSystem>() != null) return;
            GameObject go = new GameObject("EpicSparkEventSystem");
            go.AddComponent<EventSystem>();
            go.AddComponent<StandaloneInputModule>();
        }
    }
}
