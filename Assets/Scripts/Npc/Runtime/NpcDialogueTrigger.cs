// NPC 对话触发：玩家在 3m 内按鼠标右键交谈（与星火神殿操作一致）；对话服务由上层通过静态委托注入。
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
        /// 输入 NPC 描述与玩家发言，返回 NPC 回复文本。
        /// 为 null 时走本地降级文案。
        /// </summary>
        public static Func<NpcSpec, string, Task<string>> ReplyHandler;

        /// <summary>对话结束回调：携带 NPC 描述与本轮对话历史，供上层做概括转述。</summary>
        public static Action<NpcSpec, List<string>> ConversationEnded;

        /// <summary>当前正在进行的对话（同一时间只允许一场）。</summary>
        public static NpcDialogueTrigger ActiveConversation { get; private set; }

        [Header("设置")]
        public float TalkRange = 3f;
        [Tooltip("与星火神殿一致：鼠标右键交谈")]
        public KeyCode TalkKey = KeyCode.Mouse1;

        [Header("状态")]
        public bool CanTalk = true;

        public NpcSpec Spec { get; private set; }
        public bool InConversation { get; private set; }
        public IReadOnlyList<string> History { get { return _history; } }

        private readonly List<string> _history = new List<string>();
        private int _session;
        private int _lastToggleFrame = -1;
        private bool _frozePlayer;

        public string InteractPrompt
        {
            get
            {
                if (!CanTalk) return "";
                string label = Label;
                if (InConversation) return "按 " + KeyLabel(TalkKey) + " 结束与「" + label + "」的对话";
                return "按 " + KeyLabel(TalkKey) + " 与「" + label + "」交谈";
            }
        }

        private static string KeyLabel(KeyCode key)
        {
            if (key == KeyCode.Mouse0) return "鼠标左键";
            if (key == KeyCode.Mouse1) return "鼠标右键";
            if (key == KeyCode.Mouse2) return "鼠标中键";
            return key.ToString();
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

            if (InConversation)
            {
                if (toggledThisFrame) return;
                if (Input.GetKeyDown(TalkKey) || PlayerTooFar()) EndConversation();
                return;
            }

            EpicSparkPlayerController player = RuntimeContext.Player;
            if (player == null || !player.InputEnabled) return;
            if (!InRange(player.transform.position)) return;
            if (Input.GetKeyDown(TalkKey))
            {
                // 准星正对着别的可交互对象时，右键优先交给它（避免隔着 NPC 误开对话）。
                if (player.CurrentInteractable != null && !(player.CurrentInteractable is NpcDialogueTrigger)) return;
                ToggleConversation();
            }
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
            _history.Clear();
            InConversation = true;
            ActiveConversation = this;

            if (player != null)
            {
                player.SetInputEnabled(false);
                _frozePlayer = true;
            }

            if (HudController.Instance != null)
                HudController.Instance.SetInteractPrompt("按 " + KeyLabel(TalkKey) + " 结束与「" + Label + "」的对话");

            string opening = "你走近「" + Label + "」，向对方开口搭话。";
            _history.Add(opening);
            GameEvents.RaiseNarrative(opening);
            LogTalk("开始与「" + Label + "」交谈");

            SendAsync(_session);
        }

        public void EndConversation()
        {
            if (!InConversation) return;
            _lastToggleFrame = Time.frameCount;
            _session++;

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

        private async void SendAsync(int session)
        {
            string prompt = "（玩家走近并打招呼，想听你说点什么。请以「" + Label + "」的身份回答）";
            Func<NpcSpec, string, Task<string>> handler = ReplyHandler;
            string reply;

            if (handler == null || Spec == null)
            {
                reply = "……（" + Label + " 只是看了你一眼，没有说话。对话服务尚未接线。）";
            }
            else
            {
                try
                {
                    reply = await handler(Spec, prompt);
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[EpicSpark] NPC 对话服务异常：" + e.Message);
                    reply = "……（" + Label + " 似乎没听清。）";
                }
            }

            if (this == null) return;
            if (session != _session || !InConversation) return;
            if (string.IsNullOrEmpty(reply)) reply = "……";

            string line = Label + "：" + reply;
            _history.Add(line);
            GameEvents.RaiseNarrative(line);
            LogTalk(reply);
        }

        // ───────────────────────── 工具方法 ─────────────────────────

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
