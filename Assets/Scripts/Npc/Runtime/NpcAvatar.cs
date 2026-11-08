// NPC 运行时：沿 NpcPath 的 waypoints 巡逻（可循环），到点随机停留 1~3 秒，靠近玩家时转头看向玩家。
using System.Collections.Generic;
using UnityEngine;
using EpicSpark.Core;
using EpicSpark.Player;
using EpicSpark.Tools;

namespace EpicSpark.Npc.Runtime
{
    /// <summary>NPC 外观与行为。坐标约定：平面 (x, y) → 世界 (x, z)。</summary>
    public class NpcAvatar : MonoBehaviour
    {
        /// <summary>NPC 描述（剧情&核心 Agent 生成）。</summary>
        public NpcSpec Spec { get; private set; }

        [Header("巡逻")]
        public float arriveThreshold = 0.35f;
        public float waypointPauseMin = 1f;
        public float waypointPauseMax = 3f;
        public bool snapToGround = false;
        public float groundProbeHeight = 4f;

        [Header("感知")]
        public float lookRadius = 7f;
        public float turnSpeed = 7f;

        public NpcHealth Health { get; private set; }
        public NpcDialogueTrigger Dialogue { get; private set; }
        public ToolInventory Tool { get; private set; }
        public ToolKind HeldTool { get; private set; }
        public bool MovementEnabled { get; private set; }

        private readonly List<Vector3> _waypoints = new List<Vector3>();
        private bool _loop = true;
        private float _speed = 1.2f;
        private int _index;
        private float _pauseUntil;
        private CharacterController _controller;
        private bool _initialized;
        /// <summary>被空气墙 / 墙体挡住且几乎没有位移的累计时间：超过阈值就跳过当前路点，避免 NPC 卡死。</summary>
        private float _stuckSeconds;

        private void Awake()
        {
            MovementEnabled = true;
            _controller = GetComponent<CharacterController>();
        }

        // ───────────────────────── 初始化（Lead 的装配阶段调用） ─────────────────────────

        /// <summary>绑定 NPC 描述、巡逻路径与速度。</summary>
        public void Initialize(NpcSpec spec, List<Vector2> waypoints, bool loop, float speed)
        {
            Spec = spec;
            HeldTool = spec != null ? spec.heldTool : ToolKind.None;
            _initialized = true;

            if (spec != null && !string.IsNullOrEmpty(spec.npcId))
                gameObject.name = "Npc_" + spec.npcId;

            if (_controller == null) _controller = GetComponent<CharacterController>();

            EnsureCollider();
            EnsureHealth();
            EnsureDialogue();
            EnsureTool();

            SetPath(waypoints, loop, speed);
        }

        /// <summary>设置巡逻路径。waypoints 为世界平面坐标 (x, z)，高度取当前物体高度。</summary>
        public void SetPath(List<Vector2> waypoints, bool loop, float speed)
        {
            _waypoints.Clear();
            if (waypoints != null)
            {
                float y = transform.position.y;
                for (int i = 0; i < waypoints.Count; i++)
                    _waypoints.Add(new Vector3(waypoints[i].x, y, waypoints[i].y));
            }
            _loop = loop;
            _speed = Mathf.Max(0.05f, speed);
            _index = 0;
            _pauseUntil = 0f;
            _stuckSeconds = 0f;
        }

        public void SetMovementEnabled(bool enabled)
        {
            MovementEnabled = enabled;
        }

        /// <summary>强制立刻转头看向某点（例如对话开始）。</summary>
        public void LookAtPoint(Vector3 worldPoint)
        {
            Vector3 direction = worldPoint - transform.position;
            direction.y = 0f;
            if (direction.sqrMagnitude < 0.0004f) return;
            transform.rotation = Quaternion.LookRotation(direction.normalized, Vector3.up);
        }

        // ───────────────────────── 每帧行为 ─────────────────────────

        private void Update()
        {
            if (!_initialized || !MovementEnabled) return;
            if (Health != null && Health.IsDead) return;

            EpicSparkPlayerController player = RuntimeContext.Player;
            if (player != null)
            {
                float distance = FlatDistance(transform.position, player.transform.position);
                if (distance <= lookRadius)
                {
                    SmoothLookAt(player.transform.position);
                    return;
                }
            }

            Patrol();
        }

        private void Patrol()
        {
            if (_waypoints.Count == 0) return;
            if (Time.time < _pauseUntil)
            {
                _stuckSeconds = 0f;
                return;
            }

            if (_index >= _waypoints.Count)
            {
                if (_loop) _index = 0;
                else
                {
                    _index = _waypoints.Count - 1;
                    return;
                }
            }

            Vector3 target = _waypoints[_index];
            Vector3 flatPosition = new Vector3(transform.position.x, 0f, transform.position.z);
            Vector3 flatTarget = new Vector3(target.x, 0f, target.z);
            Vector3 toTarget = flatTarget - flatPosition;

            if (toTarget.magnitude <= arriveThreshold)
            {
                _pauseUntil = Time.time + Random.Range(waypointPauseMin, Mathf.Max(waypointPauseMin, waypointPauseMax));
                _index++;
                _stuckSeconds = 0f;
                return;
            }

            Vector3 direction = toTarget.normalized;
            Vector3 next = transform.position + direction * _speed * Time.deltaTime;
            if (snapToGround) next.y = SampleGround(next.y);

            Vector3 before = transform.position;
            MoveTo(next);
            SmoothLookAt(transform.position + direction * 2f);

            // 模型空气墙 / 场景墙体挡住去路时，CharacterController 会停在原地。
            // 持续 1.2 秒几乎没动就放弃当前路点，防止 NPC 永远顶在墙上（不穿模，也不卡死）。
            Vector3 moved = transform.position - before;
            moved.y = 0f;
            float expected = _speed * Mathf.Max(0.0001f, Time.deltaTime) * 0.25f;
            if (moved.magnitude < expected)
            {
                _stuckSeconds += Time.deltaTime;
                if (_stuckSeconds > 1.2f)
                {
                    _stuckSeconds = 0f;
                    _index++;
                    _pauseUntil = Time.time + 0.2f;
                }
            }
            else
            {
                _stuckSeconds = 0f;
            }
        }

        private void MoveTo(Vector3 worldPosition)
        {
            if (_controller != null && _controller.enabled)
            {
                _controller.Move(worldPosition - transform.position);
                return;
            }
            transform.position = worldPosition;
        }

        private void SmoothLookAt(Vector3 worldPoint)
        {
            Vector3 direction = worldPoint - transform.position;
            direction.y = 0f;
            if (direction.sqrMagnitude < 0.0004f) return;
            Quaternion target = Quaternion.LookRotation(direction.normalized, Vector3.up);
            transform.rotation = Quaternion.Slerp(transform.rotation, target, Time.deltaTime * turnSpeed);
        }

        private float SampleGround(float fromY)
        {
            RaycastHit hit;
            Vector3 origin = new Vector3(transform.position.x, fromY + groundProbeHeight, transform.position.z);
            if (Physics.Raycast(origin, Vector3.down, out hit, groundProbeHeight * 2f, ~0, QueryTriggerInteraction.Ignore))
            {
                if (hit.normal.y > 0.6f) return hit.point.y;
            }
            return fromY;
        }

        // ───────────────────────── 组件装配 ─────────────────────────

        private void EnsureCollider()
        {
            if (GetComponent<Collider>() != null) return;
            if (GetComponentInChildren<Collider>() != null) return;
            CapsuleCollider capsule = gameObject.AddComponent<CapsuleCollider>();
            capsule.height = 1.8f;
            capsule.radius = 0.35f;
            capsule.center = new Vector3(0f, 0.9f, 0f);
        }

        private void EnsureHealth()
        {
            Health = GetComponent<NpcHealth>();
            if (Health == null) Health = gameObject.AddComponent<NpcHealth>();
            float maxHp = Spec != null && Spec.maxHp > 0f ? Spec.maxHp : 100f;
            string npcId = Spec != null ? Spec.npcId : "";
            Health.Initialize(maxHp, npcId);
            if (Spec != null && !string.IsNullOrEmpty(Spec.displayName)) Health.DisplayName = Spec.displayName;
            Health.EnsureVisibleBar();
        }

        private void EnsureDialogue()
        {
            Dialogue = GetComponent<NpcDialogueTrigger>();
            if (Dialogue == null) Dialogue = gameObject.AddComponent<NpcDialogueTrigger>();
            Dialogue.Initialize(Spec);
        }

        private void EnsureTool()
        {
            if (HeldTool == ToolKind.None) return;
            Tool = GetComponent<ToolInventory>();
            if (Tool == null) Tool = gameObject.AddComponent<ToolInventory>();
            Tool.ActorId = Spec != null && !string.IsNullOrEmpty(Spec.npcId) ? Spec.npcId : name;
            Tool.startingTool = HeldTool;
            Tool.EquipSilent(HeldTool);
        }

        private static float FlatDistance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        private void OnDrawGizmosSelected()
        {
            if (_waypoints.Count == 0) return;
            Gizmos.color = Color.cyan;
            Vector3 previous = transform.position;
            for (int i = 0; i < _waypoints.Count; i++)
            {
                Gizmos.DrawSphere(_waypoints[i], 0.15f);
                Gizmos.DrawLine(previous, _waypoints[i]);
                previous = _waypoints[i];
            }
            if (_loop && _waypoints.Count > 1) Gizmos.DrawLine(previous, _waypoints[0]);
        }
    }
}
