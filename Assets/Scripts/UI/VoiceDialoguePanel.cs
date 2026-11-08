// 语音对话面板：屏幕底部居中的对话条，显示 NPC 名 / 状态 / 玩家与 NPC 台词 /
// 打字输入框 / 操作提示 / 麦克风音量条。
// 纯 uGUI 代码构建，不依赖 TMP、字体资源与预制体；不引用 EpicSpark.Voice，
// 避免与语音控制器产生循环依赖。
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace EpicSpark.UI
{
    /// <summary>面板状态色调，用于状态文字、顶部强调线与音量条着色。</summary>
    public enum VoicePanelTone
    {
        Idle,
        Listening,
        Busy,
        Speaking,
        Error
    }

    /// <summary>NPC 语音对话的显示与输入面板。控制器通过公开方法驱动，自身不判定业务逻辑。</summary>
    public class VoiceDialoguePanel : MonoBehaviour
    {
        /// <summary>玩家按下 Enter 且输入了非空文本时触发，参数为去掉首尾空白后的文本。</summary>
        public event System.Action<string> TextSubmitted;

        /// <summary>输入框聚焦时按下 Esc 触发（随后自动失焦）。</summary>
        public event System.Action TextInputCancelled;

        // ───────────────────────── 布局常量 ─────────────────────────

        private const float PanelWidth = 1200f;
        private const float PanelBottom = 24f;
        private const float PanelHeight = 320f;
        private const float InnerWidth = 1160f;
        private const float MicTrackWidth = 240f;

        private const string DefaultHint = "按住 V 说话 · 松开结束 · T 打字 · Enter 发送 · F 结束对话";

        // 配色：半透明深色底 + 青绿强调色，与项目其他 uGUI 面板保持一致。
        private static readonly Color PanelColor = new Color(0.055f, 0.066f, 0.085f, 0.92f);
        private static readonly Color AccentColor = new Color(0.16f, 0.82f, 0.74f, 1f);
        private static readonly Color DividerColor = new Color(1f, 1f, 1f, 0.10f);
        private static readonly Color TitleColor = new Color(0.94f, 0.95f, 0.96f, 1f);
        private static readonly Color HintColor = new Color(0.55f, 0.62f, 0.68f, 1f);
        private static readonly Color PlayerFinalColor = new Color(0.84f, 0.92f, 1f, 0.98f);
        private static readonly Color PlayerInterimColor = new Color(0.66f, 0.76f, 0.86f, 0.92f);
        private static readonly Color NpcColor = new Color(1f, 0.87f, 0.70f, 0.98f);
        private static readonly Color InputBgColor = new Color(0.10f, 0.12f, 0.15f, 0.96f);
        private static readonly Color PlaceholderColor = new Color(0.42f, 0.46f, 0.50f, 1f);
        private static readonly Color SendNormalColor = new Color(0.10f, 0.62f, 0.55f, 1f);
        private static readonly Color MicTrackColor = new Color(1f, 1f, 1f, 0.10f);

        // ───────────────────────── 节点引用 ─────────────────────────

        private Canvas _canvas;
        private GameObject _root;
        private Image _accentLine;
        private Text _titleText;
        private Text _statusText;
        private Text _playerLine;
        private Text _npcLine;
        private Text _hintText;
        private InputField _input;
        private Image _micFill;

        // ───────────────────────── 运行状态 ─────────────────────────

        private string _npcName = "NPC";
        private bool _built;
        private bool _suppressEndEdit;
        private bool _pendingFocus;
        private bool _hasTextFocus;
        private int _submitFrame = -1;
        private float _micTarget;
        private float _micShown;

        // ───────────────────────── 公开属性 ─────────────────────────

        /// <summary>面板根节点是否处于激活状态。</summary>
        public bool IsVisible { get { return _root != null && _root.activeSelf; } }

        /// <summary>输入框当前是否聚焦（正在打字）。</summary>
        public bool IsTextInputActive { get { return _input != null && _input.isFocused; } }

        /// <summary>输入框当前文本（未聚焦时也可读）。</summary>
        public string CurrentInputText { get { return _input != null ? _input.text : ""; } }

        // ───────────────────────── 生命周期 ─────────────────────────

        private void Awake()
        {
            EnsureBuilt();
        }

        private void OnDestroy()
        {
            // Canvas 挂在外部父节点下时，跟随面板一起清理，避免残留空画布。
            if (_canvas != null) Destroy(_canvas.gameObject);
        }

        private void Update()
        {
            if (_root == null || !_root.activeSelf) return;

            // 音量条平滑过渡
            float dt = Time.unscaledDeltaTime;
            _micShown = Mathf.Lerp(_micShown, Mathf.Clamp01(_micTarget), Mathf.Clamp01(dt * 12f));
            ApplyMicBarWidth();

            HandleInputKeys();

            // ActivateInputField 有时需要下一帧才真正生效，这里补一次。
            if (_pendingFocus)
            {
                _pendingFocus = false;
                ApplyTextInputFocus();
            }
        }

        // ───────────────────────── 工厂 ─────────────────────────

        /// <summary>
        /// 创建面板。Canvas 挂在传入 parent 下（parent 为 null 时挂到自身 transform 下），
        /// 这样场景切换 / 控制器销毁时面板一起消失。创建后面板默认隐藏。
        /// </summary>
        public static VoiceDialoguePanel Create(Transform parent)
        {
            GameObject go = new GameObject("VoiceDialoguePanel", typeof(RectTransform));
            if (parent != null) go.transform.SetParent(parent, false);

            VoiceDialoguePanel panel = go.AddComponent<VoiceDialoguePanel>();
            panel.EnsureBuilt();
            panel.Hide();
            return panel;
        }

        // ───────────────────────── 显示控制 ─────────────────────────

        /// <summary>显示面板，并更新标题里的 NPC 名。</summary>
        public void Show(string npcName)
        {
            EnsureBuilt();
            _npcName = string.IsNullOrEmpty(npcName) ? "NPC" : npcName.Trim();
            RefreshTitle();

            if (_root != null) _root.SetActive(true);
            ApplyMicBarWidth();
        }

        /// <summary>隐藏面板：清空输入框并取消聚焦（台词由 ClearConversation 负责清理）。</summary>
        public void Hide()
        {
            _pendingFocus = false;
            _hasTextFocus = false;

            if (_input != null)
            {
                _suppressEndEdit = true;
                _input.text = "";
                _input.DeactivateInputField();
                _suppressEndEdit = false;
            }

            if (EventSystem.current != null && _input != null &&
                EventSystem.current.currentSelectedGameObject == _input.gameObject)
            {
                EventSystem.current.SetSelectedGameObject(null);
            }

            _micTarget = 0f;
            _micShown = 0f;
            ApplyMicBarWidth();

            if (_root != null) _root.SetActive(false);
        }

        // ───────────────────────── 文本更新接口 ─────────────────────────

        /// <summary>设置状态行文字与色调（Idle 灰蓝 / Listening 橙红 / Busy 金黄 / Speaking 亮蓝 / Error 红）。</summary>
        public void SetStatus(string status, VoicePanelTone tone)
        {
            EnsureBuilt();

            Color toneColor = ResolveToneColor(tone);
            if (_statusText != null)
            {
                _statusText.text = status == null ? "" : status;
                _statusText.color = toneColor;
            }
            if (_accentLine != null) _accentLine.color = WithAlpha(toneColor, 0.95f);
            if (_micFill != null) _micFill.color = WithAlpha(toneColor, 0.95f);
        }

        /// <summary>
        /// 设置玩家台词。finalized=false 表示 ASR 中间结果，会在末尾标注“（识别中）”并降低亮度。
        /// </summary>
        public void SetPlayerLine(string text, bool finalized)
        {
            EnsureBuilt();
            if (_playerLine == null) return;

            string body = text == null ? "" : text.Trim();
            if (body.Length == 0)
            {
                _playerLine.text = "";
                return;
            }

            _playerLine.text = "你：" + body + (finalized ? "" : "（识别中）");
            _playerLine.color = finalized ? PlayerFinalColor : PlayerInterimColor;
        }

        /// <summary>设置 NPC 台词（自动带 NPC 名前缀）。</summary>
        public void SetNpcLine(string text)
        {
            EnsureBuilt();
            if (_npcLine == null) return;

            string body = text == null ? "" : text.Trim();
            _npcLine.text = body.Length == 0 ? "" : "「" + _npcName + "」：" + body;
            _npcLine.color = NpcColor;
        }

        /// <summary>设置底部操作提示；传 null 或空串恢复默认提示。</summary>
        public void SetHint(string hint)
        {
            EnsureBuilt();
            if (_hintText == null) return;
            _hintText.text = string.IsNullOrEmpty(hint) ? DefaultHint : hint;
        }

        /// <summary>麦克风音量 0..1，驱动音量条宽度（内部做了平滑）。</summary>
        public void SetMicLevel(float level01)
        {
            _micTarget = Mathf.Clamp01(level01);
        }

        /// <summary>清空玩家与 NPC 台词（输入框内容不受影响）。</summary>
        public void ClearConversation()
        {
            EnsureBuilt();
            if (_playerLine != null) _playerLine.text = "";
            if (_npcLine != null) _npcLine.text = "";
        }

        // ───────────────────────── 输入焦点控制 ─────────────────────────

        /// <summary>激活输入框（T 键调用）。面板隐藏时会先自动显示。</summary>
        public void FocusTextInput()
        {
            EnsureBuilt();
            if (!IsVisible) Show(_npcName);

            _pendingFocus = true;
            ApplyTextInputFocus();
        }

        /// <summary>让输入框失焦（不改动已有文本）。</summary>
        public void BlurTextInput()
        {
            _pendingFocus = false;
            _hasTextFocus = false;
            if (_input == null) return;

            _suppressEndEdit = true;
            _input.DeactivateInputField();
            _suppressEndEdit = false;

            if (EventSystem.current != null &&
                EventSystem.current.currentSelectedGameObject == _input.gameObject)
            {
                EventSystem.current.SetSelectedGameObject(null);
            }
        }

        // ───────────────────────── 键盘 / 提交 ─────────────────────────

        private void HandleInputKeys()
        {
            if (_input == null) return;

            bool focused = _input.isFocused;
            if (focused) _hasTextFocus = true;

            // Esc：仅在输入框处于聚焦状态时取消输入
            if (_hasTextFocus && Input.GetKeyDown(KeyCode.Escape))
            {
                CancelTextInput();
                return;
            }
            if (!focused) _hasTextFocus = false;

            // Enter：提交（onEndEdit 里也会兜底，_submitFrame 去重）
            if (focused && (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)))
            {
                SubmitCurrentInput();
            }
        }

        /// <summary>legacy InputField 在单行模式下回车也会结束编辑，这里用按键状态区分“回车提交”和“失焦”。</summary>
        private void HandleEndEdit(string value)
        {
            if (_suppressEndEdit) return;
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
            {
                SubmitCurrentInput();
            }
        }

        private void OnSendClicked()
        {
            SubmitCurrentInput();
        }

        private void SubmitCurrentInput()
        {
            if (_input == null) return;
            if (_submitFrame == Time.frameCount) return; // 同一帧只提交一次
            _submitFrame = Time.frameCount;

            string text = _input.text;
            if (string.IsNullOrEmpty(text)) return;
            text = text.Trim();
            if (text.Length == 0) return;

            _suppressEndEdit = true;
            _input.text = "";
            _suppressEndEdit = false;

            // 提交后是否恢复焦点由调用方在事件里决定（可调用 FocusTextInput / BlurTextInput）。
            _hasTextFocus = false;

            System.Action<string> handler = TextSubmitted;
            if (handler != null) handler(text);
        }

        private void CancelTextInput()
        {
            _hasTextFocus = false;
            _pendingFocus = false;

            if (_input != null)
            {
                _suppressEndEdit = true;
                _input.DeactivateInputField();
                _suppressEndEdit = false;
            }
            if (EventSystem.current != null && _input != null &&
                EventSystem.current.currentSelectedGameObject == _input.gameObject)
            {
                EventSystem.current.SetSelectedGameObject(null);
            }

            System.Action handler = TextInputCancelled;
            if (handler != null) handler();
        }

        private void ApplyTextInputFocus()
        {
            if (_input == null || !IsVisible) return;

            if (EventSystem.current != null)
            {
                EventSystem.current.SetSelectedGameObject(_input.gameObject);
            }
            _input.Select();
            _input.ActivateInputField();
            _hasTextFocus = true;
        }

        // ───────────────────────── 构建 ─────────────────────────

        private void EnsureBuilt()
        {
            if (_built) return;
            Build();
        }

        private void Build()
        {
            if (_built) return;
            _built = true;

            Transform canvasParent = transform.parent != null ? transform.parent : transform;
            EnsureEventSystem(canvasParent);

            // 独立 Canvas，排在 HUD（10）之上、设置面板（120）之下
            _canvas = UiFactory.CreateCanvas("VoiceDialogueCanvas", 110, true);
            _canvas.transform.SetParent(canvasParent, false);

            // 面板根：屏幕底部居中
            RectTransform panel = UiFactory.NewRect("Panel", _canvas.transform);
            UiFactory.SetAnchors(panel,
                new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                new Vector2(0f, PanelBottom), new Vector2(PanelWidth, PanelHeight));

            Image background = panel.gameObject.AddComponent<Image>();
            background.color = PanelColor;
            background.raycastTarget = true;
            _root = panel.gameObject;

            // 顶部强调线（颜色随状态变化）
            _accentLine = UiFactory.NewImage("AccentLine", panel, AccentColor);
            UiFactory.SetAnchors(_accentLine.rectTransform,
                new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f),
                Vector2.zero, new Vector2(0f, 3f));

            // 标题：与「NPC 名」的对话
            _titleText = UiFactory.NewText("Title", panel, 26, TitleColor, TextAnchor.UpperLeft);
            UiFactory.SetAnchors(_titleText.rectTransform,
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(20f, -18f), new Vector2(760f, 36f));
            _titleText.fontStyle = FontStyle.Bold;
            _titleText.horizontalOverflow = HorizontalWrapMode.Overflow;
            RefreshTitle();

            // 状态行（右对齐）
            _statusText = UiFactory.NewText("Status", panel, 20, ResolveToneColor(VoicePanelTone.Idle),
                TextAnchor.UpperRight);
            UiFactory.SetAnchors(_statusText.rectTransform,
                new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(1f, 1f),
                new Vector2(-20f, -20f), new Vector2(380f, 30f));
            _statusText.horizontalOverflow = HorizontalWrapMode.Overflow;
            _statusText.text = "待命中";

            // 玩家台词行
            _playerLine = UiFactory.NewText("PlayerLine", panel, 22, PlayerFinalColor, TextAnchor.UpperLeft);
            UiFactory.SetAnchors(_playerLine.rectTransform,
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(20f, -66f), new Vector2(InnerWidth, 30f));
            _playerLine.horizontalOverflow = HorizontalWrapMode.Overflow;

            // NPC 台词行
            _npcLine = UiFactory.NewText("NpcLine", panel, 22, NpcColor, TextAnchor.UpperLeft);
            UiFactory.SetAnchors(_npcLine.rectTransform,
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(20f, -106f), new Vector2(InnerWidth, 30f));
            _npcLine.horizontalOverflow = HorizontalWrapMode.Overflow;

            // 分隔线与输入区分隔
            Image divider = UiFactory.NewImage("Divider", panel, DividerColor);
            UiFactory.SetAnchors(divider.rectTransform,
                new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0f, -144f), new Vector2(0f, 1f));

            BuildInputRow(panel);
            BuildMicRow(panel);
        }

        private void BuildInputRow(RectTransform panel)
        {
            // 输入框（legacy InputField，支持 Windows IME 中文）
            RectTransform inputRect = UiFactory.NewRect("TextInput", panel);
            UiFactory.SetAnchors(inputRect,
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(20f, -158f), new Vector2(1038f, 50f));

            Image inputBg = inputRect.gameObject.AddComponent<Image>();
            inputBg.color = InputBgColor;
            inputBg.raycastTarget = true;

            Text inputText = UiFactory.NewText("Text", inputRect, 22,
                new Color(0.95f, 0.96f, 0.97f, 1f), TextAnchor.MiddleLeft);
            UiFactory.Stretch(inputText.rectTransform, 14f, 6f, 14f, 6f);
            inputText.supportRichText = false;
            inputText.horizontalOverflow = HorizontalWrapMode.Overflow;

            Text placeholder = UiFactory.NewText("Placeholder", inputRect, 22,
                PlaceholderColor, TextAnchor.MiddleLeft);
            UiFactory.Stretch(placeholder.rectTransform, 14f, 6f, 14f, 6f);
            placeholder.horizontalOverflow = HorizontalWrapMode.Overflow;
            placeholder.text = "按 T 打字，Enter 发送（也支持中文输入法）";

            _input = inputRect.gameObject.AddComponent<InputField>();
            _input.textComponent = inputText;
            _input.placeholder = placeholder;
            _input.targetGraphic = inputBg;
            _input.lineType = InputField.LineType.SingleLine;
            _input.characterLimit = 200;
            _input.caretWidth = 2;
            _input.customCaretColor = true;
            _input.caretColor = AccentColor;
            _input.selectionColor = new Color(0.16f, 0.82f, 0.74f, 0.35f);
            _input.onEndEdit.AddListener(HandleEndEdit);

            // 发送按钮
            RectTransform sendRect = UiFactory.NewRect("SendButton", panel);
            UiFactory.SetAnchors(sendRect,
                new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(1f, 1f),
                new Vector2(-20f, -158f), new Vector2(110f, 50f));

            Image sendBg = sendRect.gameObject.AddComponent<Image>();
            sendBg.color = SendNormalColor;
            sendBg.raycastTarget = true;

            Button sendButton = sendRect.gameObject.AddComponent<Button>();
            sendButton.targetGraphic = sendBg;
            ColorBlock colors = sendButton.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(0.86f, 0.96f, 1f, 1f);
            colors.pressedColor = new Color(0.70f, 0.84f, 0.90f, 1f);
            colors.disabledColor = new Color(0.5f, 0.5f, 0.5f, 0.5f);
            colors.fadeDuration = 0.08f;
            sendButton.colors = colors;
            sendButton.onClick.AddListener(OnSendClicked);

            Text sendLabel = UiFactory.NewText("Label", sendRect, 22,
                new Color(1f, 1f, 1f, 0.96f), TextAnchor.MiddleCenter);
            UiFactory.Stretch(sendLabel.rectTransform, 0f, 0f, 0f, 0f);
            sendLabel.text = "发送";
            sendLabel.horizontalOverflow = HorizontalWrapMode.Overflow;

            // 操作提示行
            _hintText = UiFactory.NewText("Hint", panel, 20, HintColor, TextAnchor.MiddleLeft);
            UiFactory.SetAnchors(_hintText.rectTransform,
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(20f, -222f), new Vector2(InnerWidth, 28f));
            _hintText.horizontalOverflow = HorizontalWrapMode.Overflow;
            _hintText.text = DefaultHint;
        }

        private void BuildMicRow(RectTransform panel)
        {
            // “麦克风”标签
            Text micLabel = UiFactory.NewText("MicLabel", panel, 20, HintColor, TextAnchor.MiddleLeft);
            UiFactory.SetAnchors(micLabel.rectTransform,
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(20f, -264f), new Vector2(100f, 22f));
            micLabel.horizontalOverflow = HorizontalWrapMode.Overflow;
            micLabel.text = "麦克风";

            // 音量条轨道
            Image micTrack = UiFactory.NewImage("MicTrack", panel, MicTrackColor);
            UiFactory.SetAnchors(micTrack.rectTransform,
                new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(1f, 1f),
                new Vector2(-20f, -268f), new Vector2(MicTrackWidth, 14f));

            // 音量条填充：宽度随音量变化，内部留 2px 边距
            _micFill = UiFactory.NewImage("MicFill", micTrack.rectTransform, AccentColor);
            RectTransform fillRect = _micFill.rectTransform;
            fillRect.anchorMin = new Vector2(0f, 0f);
            fillRect.anchorMax = new Vector2(0f, 1f);
            fillRect.pivot = new Vector2(0f, 0.5f);
            fillRect.anchoredPosition = new Vector2(2f, 0f);
            fillRect.sizeDelta = new Vector2(0f, -4f);
        }

        // ───────────────────────── 小工具 ─────────────────────────

        private void RefreshTitle()
        {
            if (_titleText != null) _titleText.text = "与「" + _npcName + "」的对话";
        }

        private void ApplyMicBarWidth()
        {
            if (_micFill == null) return;
            float inner = Mathf.Max(0f, MicTrackWidth - 4f);
            _micFill.rectTransform.sizeDelta = new Vector2(inner * Mathf.Clamp01(_micShown), -4f);
        }

        private static Color ResolveToneColor(VoicePanelTone tone)
        {
            switch (tone)
            {
                case VoicePanelTone.Listening:
                    return new Color(1f, 0.44f, 0.28f, 1f);   // 橙红：正在聆听
                case VoicePanelTone.Busy:
                    return new Color(0.98f, 0.78f, 0.30f, 1f); // 金黄：请求 / 处理中
                case VoicePanelTone.Speaking:
                    return new Color(0.36f, 0.80f, 1f, 1f);   // 亮蓝：NPC 正在说话
                case VoicePanelTone.Error:
                    return new Color(1f, 0.32f, 0.32f, 1f);   // 红：出错
                default:
                    return new Color(0.62f, 0.72f, 0.82f, 1f); // 灰蓝：待命
            }
        }

        private static Color WithAlpha(Color color, float alpha)
        {
            color.a = alpha;
            return color;
        }

        /// <summary>场景里没有 EventSystem 时补一个（与 ApiSettingsScreen.EnsureEventSystem 同思路）。</summary>
        private static void EnsureEventSystem(Transform parent)
        {
            if (UnityEngine.Object.FindObjectOfType<EventSystem>() != null) return;

            GameObject go = new GameObject("EventSystem");
            if (parent != null) go.transform.SetParent(parent, false);
            go.AddComponent<EventSystem>();
            go.AddComponent<StandaloneInputModule>();
        }
    }
}
