// 传送锚点：玩家进入半径并按 E 时记录日志、推进游戏时间，并把剧情推进交给 AnchorRouter。
using UnityEngine;
using EpicSpark.Core;
using EpicSpark.Player;
using EpicSpark.Tools;
using EpicSpark.UI;

namespace EpicSpark.Anchors
{
    /// <summary>挂在锚点模型上的触发点。可视化（光环 + 光柱）由代码生成。</summary>
    public class AnchorPoint : MonoBehaviour, IInteractable
    {
        /// <summary>当前场景内所有锚点（便于调试）。</summary>
        public static readonly System.Collections.Generic.List<AnchorPoint> All =
            new System.Collections.Generic.List<AnchorPoint>();

        [Header("触发")]
        public float triggerRadius = 2.5f;
        public KeyCode interactKey = KeyCode.E;
        public float cooldownSeconds = 1f;

        [Header("可视化")]
        public bool showVisuals = true;
        public Color ringColor = new Color(0.35f, 0.85f, 1f, 1f);
        public Color columnColor = new Color(0.14f, 0.5f, 0.95f, 1f);
        public float spinSpeed = 24f;

        [Header("射线目标")]
        [Tooltip("准星射线命中的胶囊半径，应远小于 triggerRadius")]
        public float interactColliderRadius = 0.7f;
        public float interactColliderHeight = 2.2f;

        /// <summary>锚点描述（剧情&核心 Agent 生成）。</summary>
        public SceneAnchorSpec Spec { get; private set; }

        /// <summary>HUD 交互提示。</summary>
        public string InteractPrompt
        {
            get
            {
                string label = DisplayName;
                string tip = "按 " + interactKey + " 进入「" + label + "」";
                if (Spec != null && !string.IsNullOrEmpty(Spec.timeDelta)) tip += "（时间跃迁 " + Spec.timeDelta + "）";
                return tip;
            }
        }

        public string DisplayName
        {
            get
            {
                if (Spec == null) return name;
                if (!string.IsNullOrEmpty(Spec.displayName)) return Spec.displayName;
                if (!string.IsNullOrEmpty(Spec.anchorId)) return Spec.anchorId;
                return name;
            }
        }

        private CapsuleCollider _trigger;
        private Transform _visualRoot;
        private bool _built;
        private float _nextEnterTime;

        private void Awake()
        {
            All.Add(this);
            BuildVisuals();
            EnsureTrigger();
        }

        private void OnDestroy()
        {
            All.Remove(this);
        }

        /// <summary>Lead 的搭建阶段调用：绑定锚点描述并生成可视化。</summary>
        public void Initialize(SceneAnchorSpec spec)
        {
            Spec = spec;
            if (spec != null && !string.IsNullOrEmpty(spec.anchorId)) gameObject.name = "Anchor_" + spec.anchorId;
            BuildVisuals();
            EnsureTrigger();
        }

        private void Update()
        {
            if (_visualRoot != null) _visualRoot.Rotate(0f, spinSpeed * Time.deltaTime, 0f, Space.Self);

            EpicSparkPlayerController player = RuntimeContext.Player;
            if (player == null) return;
            if (!player.InputEnabled) return;
            if (!InRange(player.transform.position)) return;
            if (Input.GetKeyDown(interactKey)) TryEnter(player);
        }

        public void Interact(EpicSparkPlayerController source)
        {
            TryEnter(source);
        }

        /// <summary>玩家是否在触发半径内。</summary>
        public bool InRange(Vector3 worldPosition)
        {
            Vector3 a = transform.position;
            float dx = a.x - worldPosition.x;
            float dz = a.z - worldPosition.z;
            float dy = a.y - worldPosition.y;
            if (Mathf.Abs(dy) > triggerRadius + 3f) return false;
            return Mathf.Sqrt(dx * dx + dz * dz) <= triggerRadius;
        }

        private void TryEnter(EpicSparkPlayerController player)
        {
            if (Time.unscaledTime < _nextEnterTime) return;
            if (!InRange(player != null ? player.transform.position : transform.position))
            {
                GameEvents.RaiseNotice("再靠近锚点一些才能进入「" + DisplayName + "」");
                return;
            }

            _nextEnterTime = Time.unscaledTime + Mathf.Max(0f, cooldownSeconds);

            string anchorId = Spec != null && !string.IsNullOrEmpty(Spec.anchorId) ? Spec.anchorId : name;
            string label = DisplayName;
            string detail = "玩家进入传送锚点「" + label + "」";
            if (Spec != null && !string.IsNullOrEmpty(Spec.timeDelta))
                detail += "，时间跃迁 " + Spec.timeDelta;
            if (Spec != null && !string.IsNullOrEmpty(Spec.targetSceneName))
                detail += "，前往「" + Spec.targetSceneName + "」";

            GameLog.Record(new PlayerActionRecord
            {
                sceneId = RuntimeContext.ResolveSceneId(),
                actorId = "player",
                actionType = "enter_anchor",
                targetId = anchorId,
                detail = detail,
                gameMinutes = GameClock.TotalMinutes,
                position = transform.position,
            });

            if (Spec != null && !string.IsNullOrEmpty(Spec.timeDelta)) GameClock.AddDelta(Spec.timeDelta);

            AnchorRouter router = AnchorRouter.Instance;
            if (router == null) router = AnchorRouter.EnsureInstance();
            router.RequestEnter(Spec);
        }

        // ───────────────────────── 可视化 ─────────────────────────

        private void BuildVisuals()
        {
            if (_built || !showVisuals) return;
            _built = true;

            _visualRoot = new GameObject("AnchorVisual").transform;
            _visualRoot.SetParent(transform, false);
            _visualRoot.localPosition = Vector3.zero;

            GameObject ring = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            ring.name = "Ring";
            RemoveCollider(ring);
            ring.transform.SetParent(_visualRoot, false);
            ring.transform.localPosition = new Vector3(0f, 0.03f, 0f);
            ring.transform.localScale = new Vector3(triggerRadius * 2f, 0.02f, triggerRadius * 2f);
            RuntimeMaterialFactory.Apply(ring, ringColor);

            GameObject inner = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            inner.name = "RingInner";
            RemoveCollider(inner);
            inner.transform.SetParent(_visualRoot, false);
            inner.transform.localPosition = new Vector3(0f, 0.05f, 0f);
            inner.transform.localScale = new Vector3(triggerRadius * 1.1f, 0.015f, triggerRadius * 1.1f);
            RuntimeMaterialFactory.Apply(inner, new Color(ringColor.r, ringColor.g, ringColor.b, 1f) * 1.4f);

            GameObject column = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            column.name = "Column";
            RemoveCollider(column);
            column.transform.SetParent(_visualRoot, false);
            column.transform.localPosition = new Vector3(0f, 1.2f, 0f);
            column.transform.localScale = new Vector3(0.55f, 1.2f, 0.55f);
            RuntimeMaterialFactory.Apply(column, columnColor);
        }

        private void EnsureTrigger()
        {
            // 用一根细胶囊作为准星射线目标（半径远小于进入半径，
            // 这样玩家站在 2.5m 进入范围内时射线仍能从外部命中它）。
            if (_trigger == null) _trigger = GetComponent<CapsuleCollider>();
            if (_trigger == null) _trigger = gameObject.AddComponent<CapsuleCollider>();
            _trigger.isTrigger = true;
            _trigger.radius = Mathf.Max(0.2f, interactColliderRadius);
            _trigger.height = Mathf.Max(0.4f, interactColliderHeight);
            _trigger.direction = 1;
            _trigger.center = new Vector3(0f, Mathf.Max(0.5f, interactColliderHeight * 0.5f), 0f);
        }

        private static void RemoveCollider(GameObject go)
        {
            Collider collider = go.GetComponent<Collider>();
            if (collider != null) Destroy(collider);
        }

        private void OnValidate()
        {
            if (triggerRadius < 0.5f) triggerRadius = 0.5f;
        }
    }
}
