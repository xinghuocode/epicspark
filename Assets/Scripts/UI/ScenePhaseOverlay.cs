// 场景阶段黑屏渐变提示：灰盒规划中 / 搭建中 / 场景显现中 / 游玩中。
using System;
using UnityEngine;
using UnityEngine.UI;
using EpicSpark.Core;

namespace EpicSpark.UI
{
    /// <summary>监听 GameEvents.PhaseChanged，显示居中阶段文字并做黑屏淡入淡出。</summary>
    public class ScenePhaseOverlay : MonoBehaviour
    {
        public static ScenePhaseOverlay Instance { get; private set; }

        [Header("渐变参数")]
        public float fadeSeconds = 0.55f;
        public float overlayAlpha = 0.92f;
        public float playingFadeOutDelay = 1.2f;

        public ScenePhase CurrentPhase { get; private set; }
        public bool IsVisible { get { return _group != null && _group.alpha > 0.01f; } }

        private Canvas _canvas;
        private CanvasGroup _group;
        private Image _background;
        private Text _label;
        private Text _subLabel;
        private float _targetAlpha;
        private float _holdUntil;
        private bool _built;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.Log("[EpicSpark] 已存在 ScenePhaseOverlay，销毁重复实例。");
                Destroy(gameObject);
                return;
            }
            Instance = this;
            Build();
        }

        private void OnEnable()
        {
            GameEvents.PhaseChanged += HandlePhaseChanged;
        }

        private void OnDisable()
        {
            GameEvents.PhaseChanged -= HandlePhaseChanged;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        /// <summary>
        /// 只刷新副标题。长请求（推理型模型可能几十秒）时用来显示「已等待 N 秒」，
        /// 让玩家知道不是卡死；不影响主标题与淡入淡出。
        /// </summary>
        public void SetSubText(string text)
        {
            Build();
            if (_subLabel != null) _subLabel.text = text ?? "";
        }

        private void Build()
        {
            if (_built) return;
            _built = true;

            _canvas = UiFactory.CreateCanvas("ScenePhaseCanvas", 100, false);
            _canvas.transform.SetParent(transform, false);

            _group = _canvas.gameObject.AddComponent<CanvasGroup>();
            _group.alpha = 0f;
            _group.blocksRaycasts = false;
            _group.interactable = false;

            _background = UiFactory.NewImage("Background", _canvas.transform, new Color(0f, 0f, 0f, 1f));
            UiFactory.Stretch(_background.rectTransform, 0f, 0f, 0f, 0f);

            _label = UiFactory.NewText("Label", _canvas.transform, 46, new Color(0.95f, 0.97f, 1f, 1f), TextAnchor.MiddleCenter);
            UiFactory.SetAnchors(_label.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), new Vector2(0f, 30f), new Vector2(1400f, 80f));

            _subLabel = UiFactory.NewText("SubLabel", _canvas.transform, 22, new Color(0.75f, 0.8f, 0.9f, 1f), TextAnchor.MiddleCenter);
            UiFactory.SetAnchors(_subLabel.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), new Vector2(0f, -40f), new Vector2(1400f, 50f));

            _label.text = "";
            _subLabel.text = "";
        }

        private void Update()
        {
            if (_group == null) return;

            if (_holdUntil > 0f && Time.unscaledTime >= _holdUntil)
            {
                _holdUntil = 0f;
                _targetAlpha = 0f;
            }

            float speed = fadeSeconds > 0.01f ? 1f / fadeSeconds : 100f;
            _group.alpha = Mathf.MoveTowards(_group.alpha, _targetAlpha, Time.unscaledDeltaTime * speed);
        }

        private void HandlePhaseChanged(ScenePhase phase)
        {
            ShowPhase(phase);
        }

        /// <summary>显示某个阶段的提示。游玩阶段会在短暂停留后自动淡出。</summary>
        public void ShowPhase(ScenePhase phase)
        {
            CurrentPhase = phase;
            Build();

            switch (phase)
            {
                case ScenePhase.Idle:
                    _targetAlpha = 0f;
                    _label.text = "";
                    _subLabel.text = "";
                    _holdUntil = 0f;
                    return;
                case ScenePhase.Preparing:
                    _label.text = "剧情规划中";
                    _subLabel.text = "剧情&核心 Agent 正在生成场景与流程";
                    _targetAlpha = overlayAlpha;
                    _holdUntil = 0f;
                    break;
                case ScenePhase.Graybox:
                    _label.text = "灰盒规划中";
                    _subLabel.text = "灰盒 Agent 正在框定区域边界与动线";
                    _targetAlpha = overlayAlpha;
                    _holdUntil = 0f;
                    break;
                case ScenePhase.Build:
                    _label.text = "搭建中";
                    _subLabel.text = "搭建 Agent 正在检索与生成美术资产";
                    _targetAlpha = overlayAlpha;
                    _holdUntil = 0f;
                    break;
                case ScenePhase.Reveal:
                    _label.text = "场景显现中";
                    _subLabel.text = "贴图由近及远逐渐显现";
                    _targetAlpha = overlayAlpha;
                    _holdUntil = 0f;
                    break;
                case ScenePhase.Playing:
                    _label.text = "游玩中";
                    _subLabel.text = "";
                    _targetAlpha = overlayAlpha;
                    _holdUntil = Time.unscaledTime + Mathf.Max(0.2f, playingFadeOutDelay);
                    break;
                case ScenePhase.Ending:
                    _label.text = "剧情收束中";
                    _subLabel.text = "正在生成结局总结";
                    _targetAlpha = overlayAlpha;
                    _holdUntil = 0f;
                    break;
                case ScenePhase.Finished:
                    _label.text = "已结束";
                    _subLabel.text = "";
                    _targetAlpha = overlayAlpha;
                    _holdUntil = 0f;
                    break;
                default:
                    _targetAlpha = 0f;
                    break;
            }
        }

        /// <summary>显示自定义文字（例如加载动画的文案），holdSeconds 后自动淡出。</summary>
        public void ShowMessage(string text, string subText, float holdSeconds)
        {
            Build();
            _label.text = text ?? "";
            _subLabel.text = subText ?? "";
            _targetAlpha = overlayAlpha;
            _holdUntil = holdSeconds > 0f ? Time.unscaledTime + holdSeconds : 0f;
        }

        /// <summary>淡出黑屏。</summary>
        public void Hide()
        {
            _targetAlpha = 0f;
            _holdUntil = 0f;
        }
    }
}
