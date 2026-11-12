// 传送锚点：只负责检测交互并路由给 AnchorRouter；日志与时间推进由 GameDirector 统一执行一次。
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
        [Tooltip("与星火神殿一致：鼠标右键交互")]
        public KeyCode interactKey = KeyCode.Mouse1;
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
                string tip = "按 " + KeyLabel(interactKey) + " 进入「" + label + "」";
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
            if (!player.InputEnabled || !player.IsCursorLocked) return;
            if (!InRange(player.transform.position)) return;
            if (Input.GetKeyDown(interactKey))
            {
                // 准星正对着别的可交互对象时，让右键优先作用于它，避免误触附近锚点。
                if (player.CurrentInteractable != null && !(player.CurrentInteractable is AnchorPoint)) return;
                TryEnter(player);
            }
        }

        private static string KeyLabel(KeyCode key)
        {
            if (key == KeyCode.Mouse0) return "鼠标左键";
            if (key == KeyCode.Mouse1) return "鼠标右键";
            if (key == KeyCode.Mouse2) return "鼠标中键";
            return key.ToString();
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

            // 行为日志与时间推进统一由 GameDirector.HandleAnchorAsync 负责。
            // 这里只路由请求，避免一次交互被记录两次、timeDelta 被叠加两次。

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
