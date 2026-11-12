// EpicSpark 第一人称玩家控制器
// 操作与星火神殿开始场景保持一致：WASD 移动 / Q 奔跑 / E 呼出或锁定鼠标 / Space 跳跃 /
// 鼠标左键攻击射击 / 鼠标右键交互（锚点、NPC 对话）/ Shift 或 C 下蹲。
// 额外键位：Esc 释放鼠标（备用）、X 切换手持工具、R 换弹。
using UnityEngine;
using UnityEngine.EventSystems;
using EpicSpark.Core;
using EpicSpark.Npc.Runtime;
using EpicSpark.Tools;
using EpicSpark.UI;

namespace EpicSpark.Player
{
    /// <summary>第一人称玩家。使用 CharacterController 实现重力、地面检测与下蹲。</summary>
    [RequireComponent(typeof(CharacterController))]
    public class EpicSparkPlayerController : MonoBehaviour
    {
        public static EpicSparkPlayerController Instance { get; private set; }

        [Header("移动参数")]
        public float walkSpeed = 4.5f;
        public float sprintSpeed = 7.6f;
        public float crouchSpeed = 2.4f;
        public float jumpHeight = 1.15f;
        public float gravity = -22f;
        public float mouseSensitivity = 2.2f;

        [Header("体型参数")]
        public float standHeight = 1.8f;
        public float crouchHeight = 1.05f;
        public float standCameraHeight = 1.62f;
        public float crouchCameraHeight = 0.92f;

        [Header("交互参数")]
        public float interactDistance = 3.5f;
        public float talkDistance = 3f;

        [Header("按键")]
        [Tooltip("呼出 / 锁定鼠标（与星火神殿一致）")]
        public KeyCode cursorToggleKey = KeyCode.E;
        [Tooltip("交互 / 传送 / 对话（与星火神殿一致，用鼠标右键）")]
        public KeyCode interactKey = KeyCode.Mouse1;
        [Tooltip("兼容旧逻辑的对话键；默认关闭，对话统一走右键交互")]
        public KeyCode talkKey = KeyCode.None;
        public KeyCode jumpKey = KeyCode.Space;
        [Tooltip("奔跑（与星火神殿一致）")]
        public KeyCode sprintKey = KeyCode.Q;
        [Tooltip("下蹲（与星火神殿一致）")]
        public KeyCode crouchKey = KeyCode.LeftShift;
        public KeyCode crouchAltKey = KeyCode.C;
        public KeyCode releaseCursorKey = KeyCode.Escape;
        [Tooltip("切换手持工具（枪 / 挎包）")]
        public KeyCode switchToolKey = KeyCode.X;
        [Tooltip("换弹")]
        public KeyCode reloadKey = KeyCode.R;

        [Header("光标")]
        [Tooltip("运行时是否自动锁定鼠标。标题界面需要鼠标时请设为 false 或调用 SetCursorLocked(false)")]
        public bool lockCursorOnStart = true;

        [Header("生命（可选，供 HUD 红闪与存档同步）")]
        public float MaxHp = 100f;
        public float CurrentHp = 100f;

        /// <summary>相机变换（射击与交互射线都从这里出发）。</summary>
        public Transform CameraTransform { get; private set; }

        public CharacterController Controller { get; private set; }
        public ToolInventory Tools { get; private set; }
        public IInteractable CurrentInteractable { get; private set; }
        public bool InputEnabled { get; private set; }
        public bool IsCrouching { get; private set; }
        public bool IsSprinting { get; private set; }
        public bool IsCursorLocked { get { return Cursor.lockState == CursorLockMode.Locked; } }
        public bool IsGrounded { get { return Controller != null && Controller.isGrounded; } }

        private Transform _cameraPivot;
        private float _pitch;
        private float _verticalSpeed;
        private bool _wantsCursorLock = true;
        private int _consumedClickFrame = -1;

        // ───────────────────────── 生命周期 ─────────────────────────

        private void Awake()
        {
            if (Instance != null && Instance != this)
                Debug.LogWarning("[EpicSpark] 场景中存在多个 EpicSparkPlayerController，行为可能异常。");
            Instance = this;
            RuntimeContext.ResetCache();

            Controller = GetComponent<CharacterController>();
            Controller.height = standHeight;
            Controller.center = new Vector3(0f, standHeight * 0.5f, 0f);
            Controller.skinWidth = 0.04f;
            Controller.stepOffset = 0.35f;
            Controller.slopeLimit = 50f;

            EnsureCamera();

            Tools = GetComponent<ToolInventory>();
            if (Tools == null) Tools = gameObject.AddComponent<ToolInventory>();

            InputEnabled = true;
            if (CurrentHp <= 0f) CurrentHp = MaxHp;
        }

        private void Start()
        {
            if (lockCursorOnStart && _wantsCursorLock) SetCursorLocked(true);
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
                RuntimeContext.ResetCache();
            }
        }

        private void Update()
        {
            UpdateCursorState();

            if (InputEnabled)
            {
                UpdateLook();
                UpdateMove();
                UpdateInteraction();
                UpdateActions();
            }

            UpdateCameraHeight();
        }

        // ───────────────────────── 外部接口 ─────────────────────────

        /// <summary>场景切换 / 传送用：直接改变玩家位置与朝向（会短暂禁用 CharacterController）。</summary>
        public void Teleport(Vector3 position, Quaternion rotation)
        {
            if (Controller != null) Controller.enabled = false;

            transform.SetPositionAndRotation(position, rotation);
            _pitch = 0f;
            _verticalSpeed = 0f;
            IsCrouching = false;

            if (Controller != null)
            {
                Controller.height = standHeight;
                Controller.center = new Vector3(0f, standHeight * 0.5f, 0f);
                Controller.enabled = true;
            }
            if (_cameraPivot != null)
            {
                _cameraPivot.localRotation = Quaternion.identity;
                _cameraPivot.localPosition = new Vector3(0f, standCameraHeight, 0f);
            }

            CurrentInteractable = null;
            if (HudController.Instance != null) HudController.Instance.SetInteractPrompt(null);
        }

        /// <summary>冻结/恢复玩家输入（加载动画、对话时使用）。</summary>
        public void SetInputEnabled(bool enabled)
        {
            InputEnabled = enabled;
            if (!enabled)
            {
                if (HudController.Instance != null) HudController.Instance.SetInteractPrompt(null);
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            else if (_wantsCursorLock)
            {
                SetCursorLocked(true);
            }
        }

        /// <summary>鼠标锁定开关（标题界面需要释放鼠标）。</summary>
        public void SetCursorLocked(bool locked)
        {
            _wantsCursorLock = locked;
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }

        /// <summary>受伤：扣血、HUD 红闪、记录日志。</summary>
        public void TakeDamage(float amount, string sourceId)
        {
            if (amount <= 0f) return;
            CurrentHp = Mathf.Max(0f, CurrentHp - amount);
            if (HudController.Instance != null) HudController.Instance.FlashDamage();
            GameLog.Record(RuntimeContext.ResolveSceneId(), "damage", sourceId ?? "",
                "玩家受到 " + amount.ToString("0.#") + " 点伤害，剩余 " + CurrentHp.ToString("0.#") + "/" + MaxHp.ToString("0.#"));
        }

        /// <summary>同步血量（存档载入 / 治疗）。</summary>
        public void SetHealth(float current, float max)
        {
            if (max > 0f) MaxHp = max;
            CurrentHp = Mathf.Clamp(current, 0f, MaxHp);
        }

        // ───────────────────────── 相机 ─────────────────────────

        private void EnsureCamera()
        {
            GameObject pivotGo = new GameObject("CameraPivot");
            pivotGo.transform.SetParent(transform, false);
            pivotGo.transform.localPosition = new Vector3(0f, standCameraHeight, 0f);
            _cameraPivot = pivotGo.transform;

            Camera cam = Camera.main;
            if (cam == null) cam = Object.FindObjectOfType<Camera>();

            if (cam == null)
            {
                GameObject camGo = new GameObject("PlayerCamera");
                camGo.transform.SetParent(_cameraPivot, false);
                cam = camGo.AddComponent<Camera>();
                cam.nearClipPlane = 0.05f;
                cam.farClipPlane = 1000f;
                if (Object.FindObjectOfType<AudioListener>() == null) camGo.AddComponent<AudioListener>();
            }
            else
            {
                cam.transform.SetParent(_cameraPivot, false);
                cam.transform.localPosition = Vector3.zero;
                cam.transform.localRotation = Quaternion.identity;
                if (cam.GetComponent<AudioListener>() == null && Object.FindObjectOfType<AudioListener>() == null)
                    cam.gameObject.AddComponent<AudioListener>();
            }

            CameraTransform = cam.transform;
        }

        private void UpdateCameraHeight()
        {
            if (_cameraPivot == null) return;
            float target = IsCrouching ? crouchCameraHeight : standCameraHeight;
            Vector3 local = _cameraPivot.localPosition;
            local.y = Mathf.MoveTowards(local.y, target, Time.deltaTime * 5f);
            _cameraPivot.localPosition = local;
        }

        // ───────────────────────── 输入 ─────────────────────────

        private void UpdateCursorState()
        {
            if (InputEnabled && Input.GetKeyDown(cursorToggleKey))
            {
                SetCursorLocked(!IsCursorLocked);
                return;
            }

            if (Input.GetKeyDown(releaseCursorKey)) SetCursorLocked(false);

            if (!IsCursorLocked && Input.GetMouseButtonDown(0))
            {
                bool overUi = EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
                if (InputEnabled && !overUi)
                {
                    SetCursorLocked(true);
                    _consumedClickFrame = Time.frameCount;
                }
            }
        }

        private void UpdateLook()
        {
            if (!IsCursorLocked || CameraTransform == null) return;

            float mouseX = Input.GetAxis("Mouse X") * mouseSensitivity;
            float mouseY = Input.GetAxis("Mouse Y") * mouseSensitivity;

            transform.Rotate(0f, mouseX, 0f, Space.Self);
            _pitch = Mathf.Clamp(_pitch - mouseY, -85f, 85f);
            if (_cameraPivot != null) _cameraPivot.localRotation = Quaternion.Euler(_pitch, 0f, 0f);
        }

        private void UpdateMove()
        {
            if (Controller == null) return;

            bool wantCrouch = Input.GetKey(crouchKey) || Input.GetKey(crouchAltKey);
            if (wantCrouch && !IsCrouching) SetCrouch(true);
            else if (!wantCrouch && IsCrouching && CanStandUp()) SetCrouch(false);

            float horizontal = Input.GetAxisRaw("Horizontal");
            float vertical = Input.GetAxisRaw("Vertical");
            Vector3 direction = transform.right * horizontal + transform.forward * vertical;
            if (direction.sqrMagnitude > 1f) direction.Normalize();

            IsSprinting = Input.GetKey(sprintKey) && !IsCrouching && vertical > 0.1f;
            float speed = IsCrouching ? crouchSpeed : (IsSprinting ? sprintSpeed : walkSpeed);

            if (Controller.isGrounded && _verticalSpeed < 0f) _verticalSpeed = -2f;
            if (Controller.isGrounded && !IsCrouching && Input.GetKeyDown(jumpKey))
                _verticalSpeed = Mathf.Sqrt(2f * jumpHeight * Mathf.Abs(gravity));

            _verticalSpeed += gravity * Time.deltaTime;

            Vector3 motion = direction * speed;
            motion.y = _verticalSpeed;
            Controller.Move(motion * Time.deltaTime);
        }

        private void UpdateInteraction()
        {
            CurrentInteractable = null;
            if (CameraTransform == null) return;

            Ray ray = new Ray(CameraTransform.position, CameraTransform.forward);
            RaycastHit[] hits = Physics.RaycastAll(ray, interactDistance, ~0, QueryTriggerInteraction.Collide);
            float bestDistance = float.MaxValue;
            for (int i = 0; i < hits.Length; i++)
            {
                Collider collider = hits[i].collider;
                if (collider == null) continue;
                if (collider.transform.IsChildOf(transform)) continue;
                IInteractable candidate = collider.GetComponentInParent<IInteractable>();
                if (candidate == null) continue;
                if (hits[i].distance < bestDistance)
                {
                    bestDistance = hits[i].distance;
                    CurrentInteractable = candidate;
                }
            }

            string prompt = CurrentInteractable != null ? CurrentInteractable.InteractPrompt : null;
            if (HudController.Instance != null) HudController.Instance.SetInteractPrompt(prompt);

            if (IsCursorLocked && CurrentInteractable != null && Input.GetKeyDown(interactKey))
                CurrentInteractable.Interact(this);
        }

        private void UpdateActions()
        {
            if (Tools != null)
            {
                if (Input.GetKeyDown(switchToolKey)) Tools.CycleTool();
                if (Input.GetKeyDown(reloadKey)) Tools.Reload();
            }

            if (Input.GetKeyDown(talkKey))
            {
                NpcDialogueTrigger trigger = ResolveTalkTrigger();
                if (trigger != null) trigger.ToggleConversation();
            }

            if (Input.GetMouseButtonDown(0) && IsCursorLocked && Time.frameCount != _consumedClickFrame)
            {
                if (Tools != null && Tools.Current == ToolKind.Gun) Tools.TryFire(CameraTransform);
                else GameLog.Record(RuntimeContext.ResolveSceneId(), "attack", "", "空手挥击（未持枪）");
            }
        }

        private NpcDialogueTrigger ResolveTalkTrigger()
        {
            NpcDialogueTrigger direct = CurrentInteractable as NpcDialogueTrigger;
            if (direct != null && direct.CanTalk) return direct;

            NpcDialogueTrigger best = null;
            float bestDistance = float.MaxValue;
            NpcDialogueTrigger[] all = Object.FindObjectsOfType<NpcDialogueTrigger>();
            for (int i = 0; i < all.Length; i++)
            {
                NpcDialogueTrigger candidate = all[i];
                if (candidate == null || !candidate.CanTalk) continue;
                float distance = FlatDistance(candidate.transform.position, transform.position);
                if (distance <= talkDistance && distance < bestDistance)
                {
                    bestDistance = distance;
                    best = candidate;
                }
            }
            return best;
        }

        private static float FlatDistance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        // ───────────────────────── 下蹲 ─────────────────────────

        private void SetCrouch(bool crouch)
        {
            IsCrouching = crouch;
            float height = crouch ? crouchHeight : standHeight;
            if (Controller == null) return;
            Controller.height = height;
            Controller.center = new Vector3(0f, height * 0.5f, 0f);
        }

        private bool CanStandUp()
        {
            if (Controller == null) return true;
            float radius = Mathf.Max(0.1f, Controller.radius - 0.05f);
            Vector3 bottom = transform.position + Vector3.up * (radius + 0.05f);
            Vector3 top = transform.position + Vector3.up * (standHeight - radius);
            return !Physics.CheckCapsule(bottom, top, radius, ~0, QueryTriggerInteraction.Ignore);
        }
    }
}
