// NPC 血量：实现 IDamageable，满血不显示血条，受伤显示，归零倒地并广播。
using UnityEngine;
using EpicSpark.Core;
using EpicSpark.Tools;

namespace EpicSpark.Npc.Runtime
{
    /// <summary>NPC 生命值。倒地后禁用移动与对话。</summary>
    public class NpcHealth : MonoBehaviour, IDamageable
    {
        [Header("身份")]
        public string NpcId = "";
        public string DisplayName = "";

        [Header("血量")]
        public float MaxHp = 100f;

        [Header("血条")]
        public bool autoCreateHealthBar = true;
        public NpcHealthBar HealthBar;
        public float barHeadHeight = 2.05f;

        [Header("倒地")]
        public float downedTipAngle = 90f;
        public float downedRaise = 0.12f;

        /// <summary>当前血量。</summary>
        public float CurrentHp { get; private set; }

        /// <summary>是否已倒地。</summary>
        public bool IsDead { get; private set; }

        public bool IsDowned { get { return IsDead; } }
        public NpcSpec Spec { get; private set; }

        private NpcAvatar _avatar;
        private NpcDialogueTrigger _dialogue;
        private CharacterController _controller;

        // ───────────────────────── 初始化 ─────────────────────────

        /// <summary>初始化血量与 id（Lead 的装配阶段或 NpcAvatar 调用）。</summary>
        public void Initialize(float maxHp, string npcId)
        {
            MaxHp = maxHp > 0f ? maxHp : 100f;
            NpcId = npcId ?? "";
            CurrentHp = MaxHp;
            IsDead = false;
            EnsureBar();
            if (HealthBar != null) HealthBar.SetValue(CurrentHp, MaxHp);
        }

        /// <summary>用 NpcSpec 初始化（同时写入显示名）。</summary>
        public void Initialize(NpcSpec spec)
        {
            Spec = spec;
            if (spec != null && !string.IsNullOrEmpty(spec.displayName)) DisplayName = spec.displayName;
            Initialize(spec != null ? spec.maxHp : 100f, spec != null ? spec.npcId : "");
        }

        private void Awake()
        {
            if (CurrentHp <= 0f) CurrentHp = MaxHp;
            _avatar = GetComponent<NpcAvatar>();
            _dialogue = GetComponent<NpcDialogueTrigger>();
            if (_dialogue == null) _dialogue = GetComponentInChildren<NpcDialogueTrigger>(true);
            _controller = GetComponent<CharacterController>();
        }

        private void Start()
        {
            if (autoCreateHealthBar) EnsureBar();
            if (HealthBar != null) HealthBar.SetValue(CurrentHp, MaxHp);
        }

        // ───────────────────────── 受伤 ─────────────────────────

        /// <summary>受到伤害（IDamageable）。</summary>
        public void ApplyDamage(float amount, string sourceId)
        {
            if (IsDead || amount <= 0f) return;

            CurrentHp = Mathf.Max(0f, CurrentHp - amount);

            if (HealthBar == null) EnsureBar();
            if (HealthBar != null) HealthBar.SetValue(CurrentHp, MaxHp);

            GameEvents.RaiseNpcHealth(NpcId, CurrentHp, MaxHp);

            GameLog.Record(new PlayerActionRecord
            {
                sceneId = RuntimeContext.ResolveSceneId(),
                actorId = string.IsNullOrEmpty(sourceId) ? "player" : sourceId,
                actionType = "damage",
                targetId = NpcId,
                detail = "「" + Label + "」受到 " + amount.ToString("0.#") + " 点伤害，剩余 " +
                         CurrentHp.ToString("0.#") + "/" + MaxHp.ToString("0.#"),
                gameMinutes = GameClock.TotalMinutes,
                position = transform.position,
            });

            if (CurrentHp <= 0f) GoDown(sourceId);
        }

        /// <summary>受治疗（可选）。</summary>
        public void Heal(float amount)
        {
            if (IsDead || amount <= 0f) return;
            CurrentHp = Mathf.Clamp(CurrentHp + amount, 0f, MaxHp);
            if (HealthBar != null) HealthBar.SetValue(CurrentHp, MaxHp);
            GameEvents.RaiseNpcHealth(NpcId, CurrentHp, MaxHp);
        }

        private void GoDown(string sourceId)
        {
            if (IsDead) return;
            IsDead = true;
            CurrentHp = 0f;

            if (_avatar == null) _avatar = GetComponent<NpcAvatar>();
            if (_avatar != null)
            {
                _avatar.SetMovementEnabled(false);
                _avatar.enabled = false;
            }

            if (_dialogue == null) _dialogue = GetComponent<NpcDialogueTrigger>();
            if (_dialogue != null)
            {
                _dialogue.AbortConversation();
                _dialogue.CanTalk = false;
                _dialogue.enabled = false;
            }

            if (_controller == null) _controller = GetComponent<CharacterController>();
            if (_controller != null) _controller.enabled = false;

            transform.rotation = Quaternion.Euler(0f, transform.eulerAngles.y, downedTipAngle);
            transform.position += Vector3.up * downedRaise;

            if (HealthBar != null) HealthBar.Hide();

            GameEvents.RaiseNpcHealth(NpcId, 0f, MaxHp);
            GameLog.Record(new PlayerActionRecord
            {
                sceneId = RuntimeContext.ResolveSceneId(),
                actorId = string.IsNullOrEmpty(sourceId) ? "player" : sourceId,
                actionType = "down",
                targetId = NpcId,
                detail = "「" + Label + "」倒地（血量归零，已禁用移动与对话）",
                gameMinutes = GameClock.TotalMinutes,
                position = transform.position,
            });
            GameEvents.RaiseNotice("「" + Label + "」倒下了。");
        }

        private string Label
        {
            get
            {
                if (!string.IsNullOrEmpty(DisplayName)) return DisplayName;
                if (!string.IsNullOrEmpty(NpcId)) return NpcId;
                return name;
            }
        }

        /// <summary>确保血条存在并刷新（Lead 装配阶段或 NpcAvatar 调用）。</summary>
        public void EnsureVisibleBar()
        {
            EnsureBar();
            if (HealthBar != null) HealthBar.SetValue(CurrentHp, MaxHp);
        }

        private void EnsureBar()
        {
            if (!autoCreateHealthBar) return;

            if (HealthBar == null) HealthBar = GetComponentInChildren<NpcHealthBar>(true);
            if (HealthBar == null)
            {
                GameObject go = new GameObject("HealthBar");
                go.transform.SetParent(transform, false);
                HealthBar = go.AddComponent<NpcHealthBar>();
            }
            HealthBar.followTarget = transform;
            HealthBar.headHeight = barHeadHeight;
            HealthBar.DisplayName = Label;
        }
    }
}
