// HUD：准星 / 场景名与游戏时间 / 剧情文本 / 交互提示 / 受伤红闪 / FPS。
// 全部 uGUI 代码构造，不依赖 TMP、字体资源与预制体。
using UnityEngine;
using UnityEngine.UI;
using EpicSpark.Core;
using EpicSpark.Tools;

namespace EpicSpark.UI
{
    /// <summary>玩家 HUD。场景里通常只有一个实例。</summary>
    public class HudController : MonoBehaviour
    {
        public static HudController Instance { get; private set; }

        [Header("显示开关")]
        public bool showCrosshair = true;
        public bool showFps = true;
        public bool autoCreatePhaseOverlay = true;

        [Header("自动隐藏")]
        public float noticeHoldSeconds = 3.5f;

        public NarrativeView Narrative { get; private set; }
        public ToleranceSliderUi Tolerance { get; private set; }
        public ScenePhaseOverlay PhaseOverlay { get; private set; }

        /// <summary>当前容忍度滑块值（默认 0.5）。</summary>
        public float ToleranceValue { get { return Tolerance != null ? Tolerance.Value : 0.5f; } }

        /// <summary>是否正在显示交互提示。</summary>
        public bool HasPrompt { get { return _promptText != null && !string.IsNullOrEmpty(_promptText.text); } }

        private Canvas _canvas;
        private GameObject _overlayRoot;
        private Text _sceneText;
        private Text _promptText;
        private Text _noticeText;
        private Text _fpsText;
        private Image _damageFlash;
        private CanvasGroup _crosshairGroup;
        private string _sceneName = "";
        private float _damageAlpha;
        private float _clockTimer;
        private float _fpsTimer;
        private int _fpsFrames;
        private float _noticeUntil;
        private bool _built;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.Log("[EpicSpark] 已存在 HudController，销毁重复实例。");
                Destroy(gameObject);
                return;
            }
            Instance = this;
            Build();
            RuntimeContext.ResetCache();
        }

        private void OnEnable()
        {
            GameEvents.NarrativePosted += HandleNarrative;
            GameEvents.NoticePosted += HandleNotice;
        }

        private void OnDisable()
        {
            GameEvents.NarrativePosted -= HandleNarrative;
            GameEvents.NoticePosted -= HandleNotice;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (_overlayRoot != null) Destroy(_overlayRoot);
        }

        // ───────────────────────── 外部接口 ─────────────────────────

        /// <summary>设置左下角显示的场景名（Lead 的场景装配完成后调用）。</summary>
        public void SetSceneInfo(string sceneName)
        {
            _sceneName = sceneName ?? "";
            RuntimeContext.SceneName = _sceneName;
            RefreshSceneText();
        }

        /// <summary>同时设置场景 id 与显示名。</summary>
        public void SetSceneInfo(string sceneId, string sceneName)
        {
            RuntimeContext.SceneId = sceneId ?? "";
            SetSceneInfo(sceneName);
        }

        /// <summary>底部交互提示；传 null 或空串清除。</summary>
        public void SetInteractPrompt(string text)
        {
            if (_promptText == null) return;
            _promptText.text = string.IsNullOrEmpty(text) ? "" : text;
        }

        /// <summary>标题界面模式下的容忍度滑块显隐。</summary>
        public void SetToleranceSliderVisible(bool visible)
        {
            if (Tolerance == null) return;
            Tolerance.SetVisible(visible);
        }

        /// <summary>受伤红闪。</summary>
        public void FlashDamage()
        {
            _damageAlpha = 1f;
            if (_damageFlash != null)
            {
                _damageFlash.gameObject.SetActive(true);
                _damageFlash.color = new Color(0.75f, 0f, 0f, _damageAlpha * 0.55f);
            }
        }

        /// <summary>推一条剧情文本到左上角。</summary>
        public void PushNarrative(string text)
        {
            if (Narrative != null) Narrative.Push(text);
        }

        /// <summary>显示一条顶部提示（若干秒后消失）。</summary>
        public void PostNotice(string text)
        {
            if (_noticeText == null || string.IsNullOrEmpty(text)) return;
            _noticeText.text = text;
            _noticeUntil = Time.unscaledTime + Mathf.Max(0.5f, noticeHoldSeconds);
        }

        // ───────────────────────── 构建 ─────────────────────────

        private void Build()
        {
            if (_built) return;
            _built = true;

            _canvas = UiFactory.CreateCanvas("HudCanvas", 10, false);
            _canvas.transform.SetParent(transform, false);
            Transform root = _canvas.transform;

            BuildCrosshair(root);

            // 左下角：场景名 + 游戏时间 + 手持工具
            _sceneText = UiFactory.NewText("SceneInfo", root, 22, new Color(1f, 1f, 1f, 0.92f), TextAnchor.LowerLeft);
            UiFactory.SetAnchors(_sceneText.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, 0f),
                new Vector2(32f, 26f), new Vector2(760f, 110f));
            _sceneText.lineSpacing = 1.2f;

            // 底部：交互提示
            _promptText = UiFactory.NewText("Prompt", root, 24, new Color(1f, 0.95f, 0.62f, 1f), TextAnchor.LowerCenter);
            UiFactory.SetAnchors(_promptText.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                new Vector2(0f, 150f), new Vector2(1200f, 44f));

            // 底部：临时提示
            _noticeText = UiFactory.NewText("Notice", root, 22, new Color(0.72f, 0.9f, 1f, 1f), TextAnchor.LowerCenter);
            UiFactory.SetAnchors(_noticeText.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                new Vector2(0f, 206f), new Vector2(1200f, 40f));

            // 左上角：剧情文本
            Narrative = NarrativeView.Create(root, 20);

            // 右上角：FPS
            _fpsText = UiFactory.NewText("Fps", root, 18, new Color(0.8f, 1f, 0.8f, 0.8f), TextAnchor.UpperRight);
            UiFactory.SetAnchors(_fpsText.rectTransform, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(1f, 1f),
                new Vector2(-24f, -20f), new Vector2(220f, 30f));
            if (!showFps) _fpsText.gameObject.SetActive(false);

            // 全屏红闪
            _damageFlash = UiFactory.NewImage("DamageFlash", root, new Color(0.75f, 0f, 0f, 0f));
            UiFactory.Stretch(_damageFlash.rectTransform, 0f, 0f, 0f, 0f);
            _damageFlash.gameObject.SetActive(false);

            // 容忍度滑块与场景阶段提示：放在独立的根节点下，避免嵌套 Canvas 打乱渲染顺序
            _overlayRoot = new GameObject("EpicSparkOverlayUi");

            Tolerance = ToleranceSliderUi.Create(_overlayRoot.transform);
            Tolerance.SetVisible(false);

            if (autoCreatePhaseOverlay)
            {
                PhaseOverlay = ScenePhaseOverlay.Instance;
                if (PhaseOverlay == null)
                {
                    GameObject overlayGo = new GameObject("ScenePhaseOverlay");
                    overlayGo.transform.SetParent(_overlayRoot.transform, false);
                    PhaseOverlay = overlayGo.AddComponent<ScenePhaseOverlay>();
                }
            }

            RefreshSceneText();
        }

        private void BuildCrosshair(Transform root)
        {
            RectTransform cross = UiFactory.NewRect("Crosshair", root);
            UiFactory.SetAnchors(cross, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                Vector2.zero, new Vector2(48f, 48f));
            _crosshairGroup = cross.gameObject.AddComponent<CanvasGroup>();
            _crosshairGroup.alpha = 0.85f;
            _crosshairGroup.blocksRaycasts = false;

            Image horizontal = UiFactory.NewImage("Horizontal", cross, new Color(1f, 1f, 1f, 0.85f));
            UiFactory.SetAnchors(horizontal.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(16f, 2f));

            Image vertical = UiFactory.NewImage("Vertical", cross, new Color(1f, 1f, 1f, 0.85f));
            UiFactory.SetAnchors(vertical.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(2f, 16f));

            cross.gameObject.SetActive(showCrosshair);
        }

        // ───────────────────────── 每帧刷新 ─────────────────────────

        private void Update()
        {
            float dt = Time.unscaledDeltaTime;

            // 受伤红闪淡出
            if (_damageAlpha > 0f && _damageFlash != null)
            {
                _damageAlpha = Mathf.MoveTowards(_damageAlpha, 0f, dt * 1.8f);
                Color color = _damageFlash.color;
                color.a = _damageAlpha * 0.55f;
                _damageFlash.color = color;
                if (_damageAlpha <= 0f) _damageFlash.gameObject.SetActive(false);
            }

            // 时钟与手持工具：4 次/秒
            _clockTimer += dt;
            if (_clockTimer >= 0.25f)
            {
                _clockTimer = 0f;
                RefreshSceneText();
            }

            // 临时提示到期
            if (_noticeUntil > 0f && Time.unscaledTime >= _noticeUntil)
            {
                _noticeUntil = 0f;
                if (_noticeText != null) _noticeText.text = "";
            }

            // FPS
            if (showFps && _fpsText != null)
            {
                _fpsFrames++;
                _fpsTimer += dt;
                if (_fpsTimer >= 0.5f)
                {
                    float fps = _fpsFrames / Mathf.Max(0.0001f, _fpsTimer);
                    _fpsText.text = "FPS " + Mathf.RoundToInt(fps);
                    _fpsFrames = 0;
                    _fpsTimer = 0f;
                }
            }

            // 对话时隐藏准星
            if (_crosshairGroup != null)
            {
                EpicSpark.Player.EpicSparkPlayerController player = RuntimeContext.Player;
                bool inputEnabled = player == null || player.InputEnabled;
                _crosshairGroup.alpha = inputEnabled ? 0.85f : 0f;
            }
        }

        private void RefreshSceneText()
        {
            if (_sceneText == null) return;

            string sceneName = string.IsNullOrEmpty(_sceneName) ? RuntimeContext.ResolveSceneName() : _sceneName;
            string toolLine = "手持：空手";
            EpicSpark.Player.EpicSparkPlayerController player = RuntimeContext.Player;
            if (player != null && player.Tools != null) toolLine = "手持：" + player.Tools.DescribeCurrent();

            _sceneText.text = "场景：" + sceneName + "\n" + GameClock.Display() + "\n" + toolLine;
        }

        private void HandleNarrative(string text)
        {
            if (Narrative != null) Narrative.Push(text);
        }

        private void HandleNotice(string text)
        {
            PostNotice(text);
        }
    }
}
