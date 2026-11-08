// NPC 对话触发：玩家在 3m 内按 F 交谈；对话服务由上层通过静态委托注入。
// 对话开始后支持多轮：玩家发言通过 SubmitPlayerLine 送入（语音或打字），
// NPC 每句回复通过 ReplyReceived 事件广播给 HUD / 语音朗读模块。
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using EpicSpark.Core;
using EpicSpark.Player;
using EpicSpark.Tools;
using EpicSpark.UI;

namespace EpicSpark.Npc.Runtime
{
    /// <summary>NPC 对话入口。对话内容只通过 GameEvents 与委托与上层交互。</summary>
    public class NpcDialogueTrigger : MonoBehaviour, IInteractable
    {
        /// <summary>
        /// 由 Lead 在 Bootstrap 里接上 NpcDialogueService：
        /// 输入 NPC 描述、玩家发言与本轮对话历史，返回 NPC 回复文本。
        /// 为 null 时走本地降级文案。
        /// </summary>
        public static Func<NpcSpec, string, IReadOnlyList<string>, Task<string>> ReplyHandler;

        /// <summary>NPC 每说出一句（含开场白）时触发，供语音模块朗读与面板显示。</summary>
        public static event Action<NpcSpec, string> ReplyReceived;

        /// <summary>对话结束回调：携带 NPC 描述与本轮对话历史，供上层做概括转述。</summary>
        public static Action<NpcSpec, List<string>> ConversationEnded;

        /// <summary>当前正在进行的对话（同一时间只允许一场）。</summary>
        public static NpcDialogueTrigger ActiveConversation { get; private set; }

        [Header("设置")]
        public float TalkRange = 3f;
        public KeyCode TalkKey = KeyCode.F;

        [Header("状态")]
        public bool CanTalk = true;

        public NpcSpec Spec { get; private set; }
        public bool InConversation { get; private set; }
        public IReadOnlyList<string> History { get { return _history; } }

        /// <summary>是否正在等待 NPC 回复（语音面板据此显示「思考中」）。</summary>
        public bool IsAwaitingReply { get; private set; }

        private readonly List<string> _history = new List<string>();
        private int _session;
        private int _lastRequest;
        private int _lastToggleFrame = -1;
        private bool _frozePlayer;

        public string InteractPrompt
        {
            get
            {
                if (!CanTalk) return "";
                string label = Label;
                if (InConversation) return "按 " + TalkKey + " 结束与「" + label + "」的对话";
                return "按 " + TalkKey + " 与「" + label + "」交谈";
            }
        }

        private string Label
        {
            get
            {
                if (Spec != null && !string.IsNullOrEmpty(Spec.displayName)) return Spec.displayName;
                return name;
            }
        }

        public void Initialize(NpcSpec spec)
        {
            Spec = spec;
        }

        private void Update()
        {
            if (!CanTalk)
            {
                if (InConversation) AbortConversation();
                return;
            }

            bool toggledThisFrame = Time.frameCount == _lastToggleFrame;

            // 焦点在打字输入框时不要响应 F 等热键，否则玩家输入字母 f 会结束对话
            if (TextInputFocused()) return;

            if (InConversation)
            {
                if (toggledThisFrame) return;
                if (Input.GetKeyDown(TalkKey) || PlayerTooFar()) EndConversation();
                return;
            }

            EpicSparkPlayerController player = RuntimeContext.Player;
            if (player == null || !player.InputEnabled) return;
            if (!InRange(player.transform.position)) return;
            if (Input.GetKeyDown(TalkKey)) ToggleConversation();
        }

        // ───────────────────────── 交互 ─────────────────────────

        public void Interact(EpicSparkPlayerController source)
        {
            ToggleConversation();
        }

        /// <summary>开始 / 结束对话（重复调用会被帧级去重）。</summary>
        public void ToggleConversation()
        {
            if (Time.frameCount == _lastToggleFrame) return;
            _lastToggleFrame = Time.frameCount;
            if (!CanTalk) return;
            if (InConversation) EndConversation();
            else BeginConversation();
        }

        public void BeginConversation()
        {
            if (InConversation || !CanTalk) return;
            if (ActiveConversation != null && ActiveConversation != this) return;

            EpicSparkPlayerController player = RuntimeContext.Player;
            if (player != null && !player.InputEnabled) return;

            _lastToggleFrame = Time.frameCount;
            _session++;
            _lastRequest = 0;
            IsAwaitingReply = false;
            _history.Clear();
            InConversation = true;
            ActiveConversation = this;

            if (player != null)
            {
                player.SetInputEnabled(false);
                _frozePlayer = true;
            }

            if (HudController.Instance != null)
                HudController.Instance.SetInteractPrompt("按 " + TalkKey + " 结束与「" + Label + "」的对话");

            string opening = "你走近「" + Label + "」，向对方开口搭话。";
            _history.Add(opening);
            GameEvents.RaiseNarrative(opening);
            LogTalk("开始与「" + Label + "」交谈");

            SendOpeningAsync();
        }

        public void EndConversation()
        {
            if (!InConversation) return;
            _lastToggleFrame = Time.frameCount;
            _session++;
            _lastRequest++;
            IsAwaitingReply = false;

            InConversation = false;
            if (ActiveConversation == this) ActiveConversation = null;

            if (_frozePlayer)
            {
                _frozePlayer = false;
                EpicSparkPlayerController player = RuntimeContext.Player;
                if (player != null) player.SetInputEnabled(true);
            }

            if (HudController.Instance != null) HudController.Instance.SetInteractPrompt(null);

            string ending = "你结束了与「" + Label + "」的交谈。";
            GameEvents.RaiseNarrative(ending);
            LogTalk("结束与「" + Label + "」的对话，本轮共 " + _history.Count + " 句");

            Action<NpcSpec, List<string>> handler = ConversationEnded;
            if (handler != null) handler(Spec, new List<string>(_history));
        }

        /// <summary>NPC 倒地等异常情况下强制收束对话（不触发 ConversationEnded）。</summary>
        public void AbortConversation()
        {
            if (!InConversation) return;
            _session++;
            _lastRequest++;
            IsAwaitingReply = false;
            InConversation = false;
            if (ActiveConversation == this) ActiveConversation = null;

            if (_frozePlayer)
            {
                _frozePlayer = false;
                EpicSparkPlayerController player = RuntimeContext.Player;
                if (player != null) player.SetInputEnabled(true);
            }
            if (HudController.Instance != null) HudController.Instance.SetInteractPrompt(null);
        }

        // ───────────────────────── 对话内容 ─────────────────────────

        /// <summary>
        /// 玩家说了一句话（语音识别结果或键盘输入）：写入对话历史并请求 NPC 回复。
        /// 返回 false 表示当前不在对话中或内容为空。
        /// </summary>
        public bool SubmitPlayerLine(string line)
        {
            if (!InConversation || !CanTalk) return false;
            string text = (line ?? "").Trim();
            if (text.Length == 0) return false;

            int request = ++_lastRequest;
            // 交给模型的「此前对话」不含当前这句（提示词里会单独给出玩家刚说的话）
            var prior = new List<string>(_history);
            _history.Add("玩家：" + text);
            GameEvents.RaiseNarrative("你：" + text);
            LogTalk("玩家说：" + text);

            SendReplyAsync(request, text, prior);
            return true;
        }

        private async void SendOpeningAsync()
        {
            string prompt = "（玩家走近并打招呼，想听你说点什么。请以「" + Label + "」的身份回答）";
            await SendCoreAsync(0, prompt, new List<string>(_history));
        }

        private async void SendReplyAsync(int request, string playerLine, List<string> priorHistory)
        {
            await SendCoreAsync(request, playerLine, priorHistory);
        }

        /// <summary>统一的「请求 NPC 回复」流程：带会话与请求序号，过期回复直接丢弃。</summary>
        private async Task SendCoreAsync(int request, string playerLine, IReadOnlyList<string> history)
        {
            int session = _session;
            IsAwaitingReply = true;

            Func<NpcSpec, string, IReadOnlyList<string>, Task<string>> handler = ReplyHandler;
            string reply;
            if (handler == null || Spec == null)
            {
                reply = "……（" + Label + " 只是看了你一眼，没有说话。对话服务尚未接线。）";
            }
            else
            {
                try
                {
                    reply = await handler(Spec, playerLine, history ?? new List<string>());
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[EpicSpark] NPC 对话服务异常：" + e.Message);
                    reply = "……（" + Label + " 似乎没听清。）";
                }
            }

            if (this == null) return;
            if (session != _session || !InConversation) return;
            if (request != _lastRequest) return;   // 玩家已经说了更新的一句，旧回复作废
            IsAwaitingReply = false;
            PublishNpcLine(reply);
        }

        /// <summary>把 NPC 的一句话写入历史、广播叙事，并通知语音模块朗读。</summary>
        private void PublishNpcLine(string reply)
        {
            if (string.IsNullOrEmpty(reply)) reply = "……";

            string line = Label + "：" + reply;
            _history.Add(line);
            GameEvents.RaiseNarrative(line);

            Action<NpcSpec, string> handler = ReplyReceived;
            if (handler != null) handler(Spec, reply);

            LogTalk(reply);
        }

        // ───────────────────────── 工具方法 ─────────────────────────

        /// <summary>焦点在输入框时忽略 F 等热键，避免打字触发「结束对话」。</summary>
        private static bool TextInputFocused()
        {
            UnityEngine.EventSystems.EventSystem current = UnityEngine.EventSystems.EventSystem.current;
            if (current == null) return false;
            GameObject selected = current.currentSelectedGameObject;
            if (selected == null) return false;
            return selected.GetComponent<UnityEngine.UI.InputField>() != null;
        }

        private bool InRange(Vector3 position)
        {
            return FlatDistance(transform.position, position) <= TalkRange;
        }

        private bool PlayerTooFar()
        {
            EpicSparkPlayerController player = RuntimeContext.Player;
            if (player == null) return true;
            return FlatDistance(transform.position, player.transform.position) > TalkRange + 3f;
        }

        private static float FlatDistance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        private void LogTalk(string detail)
        {
            GameLog.Record(new PlayerActionRecord
            {
                sceneId = RuntimeContext.ResolveSceneId(),
                actorId = "player",
                actionType = "talk",
                targetId = Spec != null && !string.IsNullOrEmpty(Spec.npcId) ? Spec.npcId : name,
                detail = detail ?? "",
                gameMinutes = GameClock.TotalMinutes,
                position = transform.position,
            });
        }

        private void OnDisable()
        {
            if (InConversation) AbortConversation();
        }
    }
}
