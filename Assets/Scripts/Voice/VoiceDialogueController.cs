// NPC 语音对话控制器（迁移自 astrospark 的 AstroWar.Copilot.CopilotAgent）。
//
// 一轮对话的完整链路：
//   按住 V：麦克风 16kHz PCM  →  Paraformer 实时识别（DashScope run-task WS）
//   松开 V：识别出文本        →  NpcDialogueTrigger.SubmitPlayerLine（NPC 人格化 LLM 回复）
//   NPC 回复：面板显示文本     →  Qwen-TTS Realtime（24kHz PCM）→ 喇叭播放
//
// 另提供「输入模块」兜底：对话中按 T 聚焦输入框，Enter 发送（支持中文 IME）；
// 没有麦克风、没有语音 Key、识别失败时都可以继续打字与 NPC 对话。
// 再次按住 V 会打断正在朗读的回复（barge-in），并开始新一轮录音。
using System;
using System.Collections.Generic;
using EpicSpark.Core;
using EpicSpark.Npc.Runtime;
using EpicSpark.UI;
using UnityEngine;

namespace EpicSpark.Voice
{
    /// <summary>
    /// 玩家 ↔ NPC 的语音输入/朗读控制器。由 GameBootstrap 创建，全局一份。
    /// 只与 NpcDialogueTrigger 的静态事件和公开方法耦合，不直接改 NPC 模块内部状态。
    /// </summary>
    [DisallowMultipleComponent]
    public class VoiceDialogueController : MonoBehaviour
    {
        public static VoiceDialogueController Instance { get; private set; }

        public enum VoiceState { Unavailable, Idle, Recording, Recognizing, Thinking, Speaking }

        [Header("按键")]
        [Tooltip("按住说话；对话中松开即发送识别结果")]
        public KeyCode talkKey = KeyCode.V;
        [Tooltip("聚焦/切换文字输入框")]
        public KeyCode typeKey = KeyCode.T;

        [Header("行为")]
        [Tooltip("NPC 回复是否用 TTS 朗读（关闭后只有文字）")]
        public bool speakNpcReplies = true;
        [Tooltip("对话开始时 NPC 的开场白是否朗读")]
        public bool speakOpeningLine = true;

        // ───────────── 对外状态（HUD/自检/调试读取） ─────────────

        public VoiceState State { get; private set; } = VoiceState.Unavailable;
        public bool ConversationActive { get { return _trigger != null && _trigger.InConversation; } }
        public string LastUserText { get; private set; } = "";
        public string LastNpcText { get; private set; } = "";
        public string LastError { get; private set; } = "";
        public string StatusLine { get { return _status; } }
        public string ConfigSource { get { return _configSource; } }
        public VoiceConfig Config { get { return _cfg; } }
        public bool HasVoiceCredentials { get { return _cfg != null && _cfg.HasKey; } }
        public bool MicAvailable { get { return Microphone.devices != null && Microphone.devices.Length > 0; } }

        private const float TurnTimeoutSeconds = 150f;
        private const float SpeakTailSeconds = 0.5f;

        private VoiceConfig _cfg;
        private string _configSource = "";
        private NpcDialogueTrigger _trigger;
        private VoiceDialoguePanel _panel;
        private MicCapture _mic;
        private PcmPlayback _play;
        private bool _audioReady;

        // 语音识别
        private AsrClient _asr;
        private bool _asrStarted;
        private int _asrGen;
        private string _asrPartial = "";
        private string _asrFinal = "";
        private readonly List<byte[]> _preRoll = new List<byte[]>();
        private float _recordStart;

        // 语音合成
        private TtsClient _tts;
        private string _speechText = "";
        private bool _ttsConfigured;
        private bool _ttsFinished;
        private int _ttsGen;
        private float _speakIdleTimer;
        private float _turnDeadline;

        // 回复等待
        private bool _awaitingReply;
        private float _replyDeadline;
        private bool _openingPending;
        private string _status = "";

        // ─────────────────────────── 生命周期 ───────────────────────────

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning("[语音] 已存在 VoiceDialogueController，销毁重复实例。");
                Destroy(this);
                return;
            }
            Instance = this;

            _cfg = VoiceConfig.Load(out _configSource);
            Debug.Log("[语音] 配置来源: " + _configSource + " key=" + VoiceConfig.MaskKey(_cfg.apiKey)
                      + " ASR=" + _cfg.asrModel + " TTS=" + _cfg.ttsModel + "/" + _cfg.ttsVoice);

            EnsureAudio();
            EnsurePanel();
            NpcDialogueTrigger.ReplyReceived += OnNpcReply;

            State = HasVoiceCredentials && MicAvailable ? VoiceState.Idle : VoiceState.Unavailable;
            _status = IdleStatus();
            if (_panel != null) _panel.SetStatus(_status, ToneOf(State));
        }

        private void OnDestroy()
        {
            NpcDialogueTrigger.ReplyReceived -= OnNpcReply;
            if (_panel != null)
            {
                _panel.TextSubmitted -= OnTextSubmitted;
                _panel.TextInputCancelled -= OnTextInputCancelled;
            }
            CancelRecording();
            CleanupAsr();
            StopSpeaking();
            if (_mic != null) _mic.StopCapture();
            if (_play != null) _play.Shutdown();
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            DrainAsr();
            DrainTts();
            SyncConversation();

            if (!ConversationActive) return;

            // 录音中按 Esc 放弃这一句
            if (State == VoiceState.Recording && Input.GetKeyDown(KeyCode.Escape))
            {
                CancelRecording();
                return;
            }

            bool typing = _panel != null && _panel.IsTextInputActive;
            if (!typing)
            {
                if (Input.GetKeyDown(typeKey) && State != VoiceState.Recording && State != VoiceState.Recognizing)
                {
                    FocusTextInput();
                }
                else if (Input.GetKeyDown(talkKey))
                {
                    BeginRecording();
                }
                else if (Input.GetKeyUp(talkKey) && State == VoiceState.Recording)
                {
                    EndRecording();
                }
            }

            // 麦克风电平条（每帧刷新）
            if (_panel != null)
            {
                float level = State == VoiceState.Recording && _mic != null
                    ? Mathf.Clamp01(_mic.Level / Mathf.Max(0.001f, _cfg.vadThreshold * 4f))
                    : 0f;
                _panel.SetMicLevel(level);
            }

            TickState();
        }

        // ─────────────────────────── 对话生命周期 ───────────────────────────

        /// <summary>检测 NPC 对话开始/结束，切换面板与音频状态。</summary>
        private void SyncConversation()
        {
            NpcDialogueTrigger active = NpcDialogueTrigger.ActiveConversation;
            bool activeExists = active != null;
            bool currentExists = _trigger != null;

            if (!activeExists && !currentExists) return;
            if (activeExists && currentExists && active == _trigger) return;

            if (currentExists) EndConversationUi();
            _trigger = active;
            if (_trigger != null) BeginConversationUi();
        }

        private void BeginConversationUi()
        {
            LastUserText = "";
            LastNpcText = "";
            LastError = "";
            _openingPending = true;
            _awaitingReply = false;
            _status = IdleStatus();
            State = HasVoiceCredentials && MicAvailable ? VoiceState.Idle : VoiceState.Unavailable;

            if (_panel != null)
            {
                _panel.Show(TriggerName());
                _panel.ClearConversation();
                _panel.SetHint(HintText());
                _panel.SetStatus(_status, ToneOf(State));
                if (!HasVoiceCredentials || !MicAvailable) _panel.SetStatus(_status, VoicePanelTone.Error);
            }
            Debug.Log("[语音] 对话开始：" + TriggerName());
        }

        private void EndConversationUi()
        {
            CancelRecording();
            CleanupAsr();
            StopSpeaking();
            _awaitingReply = false;
            State = HasVoiceCredentials && MicAvailable ? VoiceState.Idle : VoiceState.Unavailable;
            _status = IdleStatus();
            if (_panel != null)
            {
                _panel.BlurTextInput();
                _panel.SetMicLevel(0f);
                _panel.Hide();
            }
            Debug.Log("[语音] 对话结束");
        }

        private string TriggerName()
        {
            if (_trigger == null) return "NPC";
            if (_trigger.Spec != null && !string.IsNullOrEmpty(_trigger.Spec.displayName)) return _trigger.Spec.displayName;
            return _trigger.name;
        }

        private string HintText()
        {
            return "按住 " + talkKey + " 说话 · 松开结束 · " + typeKey + " 打字输入 · Enter 发送 · F 结束对话";
        }

        private string IdleStatus()
        {
            if (!HasVoiceCredentials) return "未配置语音 API Key：可打字对话（VoiceConfig.json / DASHSCOPE_API_KEY）";
            if (!MicAvailable) return "未检测到麦克风：按 " + typeKey + " 打字输入";
            return "按住 " + talkKey + " 说话，或按 " + typeKey + " 打字";
        }

        // ─────────────────────────── 录音 → 识别 ───────────────────────────

        private void BeginRecording()
        {
            if (!ConversationActive) return;
            if (!HasVoiceCredentials)
            {
                Fail("未配置语音 API Key，只能用键盘输入。", true);
                return;
            }
            if (!MicAvailable)
            {
                Fail("未检测到可用的麦克风，只能用键盘输入。", true);
                return;
            }
            if (!_audioReady) EnsureAudio();
            if (_mic == null)
            {
                Fail("音频设备不可用，只能用键盘输入。", true);
                return;
            }

            if (!_mic.IsRunning && !_mic.StartCapture(_cfg.micDevice))
            {
                Fail("麦克风启动失败，只能用键盘输入。", true);
                return;
            }

            _asrGen++;
            CleanupAsr();
            _asrStarted = false;
            _asrPartial = "";
            _asrFinal = "";
            _preRoll.Clear();
            BargeIn();
            OpenAsr();

            LastUserText = "";
            _openingPending = false;
            if (_panel != null) _panel.SetPlayerLine("", false);
            _recordStart = Time.unscaledTime;
            State = VoiceState.Recording;
            _status = "录音中… 松开 " + talkKey + " 结束";
            UpdatePanelStatus();
        }

        private void EndRecording()
        {
            if (State != VoiceState.Recording) return;
            if (_mic != null) _mic.StopCapture();

            State = VoiceState.Recognizing;
            _status = "识别中…";
            UpdatePanelStatus();

            if (_asr == null)
            {
                FinishUtterance();
                return;
            }
            FlushPreRoll();
            _asr.Finish();
        }

        private void CancelRecording()
        {
            if (State == VoiceState.Recording || State == VoiceState.Recognizing)
            {
                if (_mic != null) _mic.StopCapture();
                CleanupAsr();
                State = HasVoiceCredentials && MicAvailable ? VoiceState.Idle : VoiceState.Unavailable;
                _status = IdleStatus();
                UpdatePanelStatus();
            }
        }

        private void OpenAsr()
        {
            CleanupAsr();
            _asr = new AsrClient(_cfg);
            _asr.Start();
        }

        private void OnMicPcm(byte[] pcm)
        {
            if (State != VoiceState.Recording || _asr == null) return;
            if (_asrStarted) _asr.SendAudio(pcm);
            else if (_preRoll.Count < 400) _preRoll.Add(pcm);   // 约 20 秒预录，等 task-started
        }

        private void FlushPreRoll()
        {
            if (_asr == null) return;
            for (int i = 0; i < _preRoll.Count; i++) _asr.SendAudio(_preRoll[i]);
            _preRoll.Clear();
        }

        /// <summary>识别结束：把文本交给 NPC 对话系统。</summary>
        private void FinishUtterance()
        {
            string utter = (_asrFinal + _asrPartial).Trim();
            _asrPartial = "";
            _asrFinal = "";
            CleanupAsr();

            if (utter.Length == 0)
            {
                State = HasVoiceCredentials && MicAvailable ? VoiceState.Idle : VoiceState.Unavailable;
                _status = "没有识别到语音，请再说一次或按 " + typeKey + " 打字";
                UpdatePanelStatus();
                return;
            }
            SubmitLine(utter);
        }

        // ─────────────────────────── 玩家发言 → NPC 回复 ───────────────────────────

        /// <summary>把玩家的一句话交给 NPC 对话模块；语音与打字两条路径共用。</summary>
        public bool SubmitLine(string text)
        {
            if (!ConversationActive || string.IsNullOrWhiteSpace(text)) return false;
            if (_trigger == null) return false;

            string line = text.Trim();
            LastUserText = line;
            _openingPending = false;
            if (_panel != null)
            {
                _panel.SetPlayerLine(line, true);
                _panel.SetNpcLine("");
            }

            // 先进入「思考中」，再提交：离线兜底回复可能是同步完成的，
            // 若先调用 SubmitPlayerLine，回复事件会抢先把状态改成「说话中」，随后又被这里覆盖。
            _awaitingReply = true;
            _replyDeadline = Time.unscaledTime + TurnTimeoutSeconds;
            State = VoiceState.Thinking;
            _status = "思考中…";
            UpdatePanelStatus();

            if (!_trigger.SubmitPlayerLine(line))
            {
                Fail("对话服务未就绪。", false);
                return false;
            }
            return true;
        }

        /// <summary>NPC 回复到达（开场白与每一句回复都会触发）。</summary>
        private void OnNpcReply(NpcSpec npc, string text)
        {
            if (_trigger == null || !ConversationActive) return;

            string line = VoiceText.Flat(text);
            if (string.IsNullOrEmpty(line)) return;

            LastNpcText = line;
            _awaitingReply = false;
            if (_panel != null) _panel.SetNpcLine(line);

            bool opening = _openingPending;
            _openingPending = false;

            // 玩家正在说下一句时，旧回复只显示不朗读，避免两个声音打架
            bool playerTalking = State == VoiceState.Recording || State == VoiceState.Recognizing;

            if (playerTalking || !speakNpcReplies || !_cfg.enableTts || !HasVoiceCredentials || (opening && !speakOpeningLine))
            {
                if (State == VoiceState.Thinking) GoIdle();
                return;
            }

            Speak(line);
        }

        // ─────────────────────────── 语音合成 → 播放 ───────────────────────────

        private void Speak(string text)
        {
            string speech = _cfg.sanitizeSpeech ? VoiceText.SanitizeForSpeech(text) : VoiceText.Flat(text);
            if (string.IsNullOrEmpty(speech))
            {
                GoIdle();
                return;
            }

            EnsureAudio();
            StopSpeaking();
            _ttsGen++;
            _tts = new TtsClient(_cfg);
            _tts.Start();

            _speechText = speech;
            _ttsConfigured = false;
            _ttsFinished = false;
            _speakIdleTimer = 0f;
            _turnDeadline = Time.unscaledTime + TurnTimeoutSeconds;
            State = VoiceState.Speaking;
            _status = "正在说话…（按住 " + talkKey + " 可打断）";
            UpdatePanelStatus();
        }

        private void SendTtsFinish()
        {
            if (_tts == null) return;
            _tts.Finish();
        }

        private void BargeIn()
        {
            // 打断：停止播放并丢弃旧 TTS 会话（_ttsGen 让迟到事件失效）
            if (_play != null) _play.Clear();
            CleanupTts();
            _ttsGen++;
        }

        private void StopSpeaking()
        {
            BargeIn();
            if (State == VoiceState.Speaking) State = HasVoiceCredentials && MicAvailable ? VoiceState.Idle : VoiceState.Unavailable;
        }

        private void GoIdle()
        {
            CleanupAsr();
            StopSpeaking();
            _awaitingReply = false;
            State = HasVoiceCredentials && MicAvailable ? VoiceState.Idle : VoiceState.Unavailable;
            _status = IdleStatus();
            UpdatePanelStatus();
        }

        // ─────────────────────────── 面板交互 ───────────────────────────

        private void FocusTextInput()
        {
            if (_panel == null) return;
            if (State == VoiceState.Recording) CancelRecording();
            _panel.FocusTextInput();
            _status = "输入中… Enter 发送，Esc 取消";
            UpdatePanelStatus();
        }

        private void OnTextSubmitted(string text)
        {
            if (_panel != null) _panel.BlurTextInput();
            if (string.IsNullOrWhiteSpace(text)) return;
            SubmitLine(text);
        }

        private void OnTextInputCancelled()
        {
            if (_panel != null) _panel.BlurTextInput();
            _status = IdleStatus();
            UpdatePanelStatus();
        }

        // ─────────────────────────── 每帧状态机 ───────────────────────────

        private void TickState()
        {
            switch (State)
            {
                case VoiceState.Recording:
                    if (Time.unscaledTime - _recordStart > _cfg.maxUtteranceSeconds) EndRecording();
                    break;

                case VoiceState.Recognizing:
                    // 正常由 task-finished 驱动；识别连接先断时兜底收尾
                    if (_asr == null || _asr.Closed) FinishUtterance();
                    break;

                case VoiceState.Thinking:
                    if (_awaitingReply && Time.unscaledTime > _replyDeadline)
                    {
                        Fail("NPC 回应超时，请重试。", false);
                    }
                    break;

                case VoiceState.Speaking:
                    // TTS 连接失败（Key 无效 / 网络不通）时别一直卡在「说话中」
                    if (!_ttsConfigured && (_tts == null || _tts.Closed))
                    {
                        _ttsFinished = true;
                        _status = "语音合成不可用，仅显示文字";
                        UpdatePanelStatus();
                    }
                    if (_play == null)
                    {
                        GoIdle();
                        break;
                    }
                    _play.PlayIfNeeded(_ttsFinished ? 0.001f : 0.15f);
                    if (_ttsFinished && _play.BufferedSeconds < 0.03f)
                    {
                        _speakIdleTimer += Time.unscaledDeltaTime;
                        if (_speakIdleTimer > SpeakTailSeconds) GoIdle();
                    }
                    else
                    {
                        _speakIdleTimer = 0f;
                    }
                    if (Time.unscaledTime > _turnDeadline)
                    {
                        StopSpeaking();
                        GoIdle();
                    }
                    break;
            }
        }

        // ─────────────────────────── 入站消息处理 ───────────────────────────

        private void DrainAsr()
        {
            AsrClient asr = _asr;
            if (asr == null) return;
            int gen = _asrGen;

            while (true)
            {
                string message;
                bool has;
                try { has = asr.TryDequeue(out message); }
                catch (Exception e) { Debug.LogException(e); return; }
                if (!has) return;
                if (gen != _asrGen) return;

                object root;
                try { root = MiniJson.Parse(message); }
                catch { continue; }
                if (root == null) continue;

                switch (MiniJson.GetString(root, "header", "event"))
                {
                    case "task-started":
                        _asrStarted = true;
                        FlushPreRoll();
                        break;

                    case "result-generated":
                        if (MiniJson.GetBool(root, "payload", "output", "sentence", "heartbeat")) break;
                        string text = MiniJson.GetString(root, "payload", "output", "sentence", "text");
                        if (string.IsNullOrEmpty(text)) break;
                        if (MiniJson.GetBool(root, "payload", "output", "sentence", "sentence_end"))
                        {
                            _asrFinal += text;
                            _asrPartial = "";
                        }
                        else
                        {
                            _asrPartial = text;
                        }
                        LastUserText = (_asrFinal + _asrPartial).Trim();
                        if (_panel != null) _panel.SetPlayerLine(LastUserText, false);
                        break;

                    case "task-finished":
                        asr.Close();
                        if (State == VoiceState.Recognizing) FinishUtterance();
                        break;

                    case "task-failed":
                        Fail("语音识别失败：" + (MiniJson.GetString(root, "header", "error_message") ?? "unknown"), true);
                        break;
                }
            }
        }

        private void DrainTts()
        {
            TtsClient tts = _tts;
            if (tts == null) return;
            int gen = _ttsGen;

            while (true)
            {
                string message;
                bool has;
                try { has = tts.TryDequeue(out message); }
                catch (Exception e) { Debug.LogException(e); return; }
                if (!has) return;
                if (gen != _ttsGen) return;

                object root;
                try { root = MiniJson.Parse(message); }
                catch { continue; }
                if (root == null) continue;

                switch (MiniJson.GetString(root, "type"))
                {
                    case "session.created":
                        tts.UpdateSession();
                        _ttsConfigured = true;
                        if (!string.IsNullOrEmpty(_speechText))
                        {
                            tts.Append(_speechText);
                            _speechText = "";
                        }
                        SendTtsFinish();
                        break;

                    case "response.audio.delta":
                        string base64 = MiniJson.GetString(root, "delta");
                        if (!string.IsNullOrEmpty(base64) && _play != null)
                        {
                            try { _play.Feed(Convert.FromBase64String(base64)); }
                            catch (FormatException) { }
                            if (State == VoiceState.Thinking)
                            {
                                State = VoiceState.Speaking;
                                _status = "正在说话…";
                                UpdatePanelStatus();
                            }
                        }
                        break;

                    case "session.finished":
                        _ttsFinished = true;
                        break;

                    case "error":
                        _ttsFinished = true;
                        if (_panel != null)
                            _panel.SetStatus("语音合成失败：" + MiniJson.GetString(root, "error", "message"), VoicePanelTone.Error);
                        break;
                }
            }
        }

        // ─────────────────────────── 工具 ───────────────────────────

        private void EnsureAudio()
        {
            if (!_audioReady)
            {
                try
                {
                    _mic = GetComponent<MicCapture>();
                    if (_mic == null) _mic = gameObject.AddComponent<MicCapture>();
                    _mic.RmsThreshold = _cfg.vadThreshold;
                    _mic.OnPcm -= OnMicPcm;
                    _mic.OnPcm += OnMicPcm;

                    _play = GetComponent<PcmPlayback>();
                    if (_play == null) _play = gameObject.AddComponent<PcmPlayback>();
                    _play.Init(Mathf.Max(8000, _cfg.ttsSampleRate));
                    _play.SetVolume(_cfg.ttsVolume);
                    _audioReady = true;
                }
                catch (Exception e)
                {
                    // 无音频设备（如 -nographics 批处理 / 虚拟机）时不应影响游戏启动
                    _audioReady = false;
                    Debug.LogWarning("[语音] 音频初始化失败（没有音频设备时属正常）: " + e.Message);
                }
            }

            if (UnityEngine.Object.FindObjectOfType<AudioListener>() == null && Camera.main != null)
                Camera.main.gameObject.AddComponent<AudioListener>();
        }

        private void EnsurePanel()
        {
            if (_panel != null) return;
            _panel = VoiceDialoguePanel.Create(transform);
            if (_panel == null) return;
            _panel.TextSubmitted += OnTextSubmitted;
            _panel.TextInputCancelled += OnTextInputCancelled;
            _panel.Hide();
        }

        private void UpdatePanelStatus()
        {
            if (_panel != null) _panel.SetStatus(_status, ToneOf(State));
        }

        private static VoicePanelTone ToneOf(VoiceState state)
        {
            switch (state)
            {
                case VoiceState.Recording: return VoicePanelTone.Listening;
                case VoiceState.Recognizing:
                case VoiceState.Thinking: return VoicePanelTone.Busy;
                case VoiceState.Speaking: return VoicePanelTone.Speaking;
                case VoiceState.Unavailable: return VoicePanelTone.Error;
                default: return VoicePanelTone.Idle;
            }
        }

        private void Fail(string message, bool focusText)
        {
            LastError = message ?? "";
            _status = LastError;
            if (_mic != null) _mic.StopCapture();
            CleanupAsr();
            BargeIn();
            _awaitingReply = false;
            State = HasVoiceCredentials && MicAvailable ? VoiceState.Idle : VoiceState.Unavailable;
            UpdatePanelStatus();
            if (_panel != null) _panel.SetStatus(LastError, VoicePanelTone.Error);
            if (focusText && _panel != null) _panel.FocusTextInput();
            Debug.LogWarning("[语音] " + LastError);
        }

        private void CleanupAsr()
        {
            if (_asr != null)
            {
                try { _asr.Close(); } catch { }
                try { _asr.Dispose(); } catch { }
                _asr = null;
            }
            _asrStarted = false;
        }

        private void CleanupTts()
        {
            if (_tts != null)
            {
                try { _tts.Close(); } catch { }
                try { _tts.Dispose(); } catch { }
                _tts = null;
            }
            _ttsConfigured = false;
            _ttsFinished = false;
            _speechText = "";
        }

        // ─────────────────────────── 自检接口 ───────────────────────────

        /// <summary>不经过麦克风的文本通道（冒烟测试 / 无麦环境用）。</summary>
        public bool SubmitTextLine(string line)
        {
            return SubmitLine(line);
        }

        /// <summary>当前状态与配置的单行诊断信息。</summary>
        public string DescribeState()
        {
            return "state=" + State + " conversation=" + ConversationActive + " key=" + (HasVoiceCredentials ? "yes" : "no")
                   + " mic=" + (MicAvailable ? "yes" : "no") + " config=" + _configSource
                   + " user=" + LastUserText + " npc=" + LastNpcText + " error=" + LastError;
        }
    }
}
