using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace SparkCode.Gameplay
{
    public enum StarAction
    {
        Forward,
        Back,
        Left,
        Right,
        Jump,
        Crouch,
        Sprint,
        Cursor,
        Attack,
        Interact
    }

    public interface IStarTempleDamageable
    {
        void TakeDamage(float amount, StarTemplePlayerController source, RaycastHit hit);
    }

    public interface IStarTempleInteractable
    {
        void Interact(StarTemplePlayerController source);
    }

    /// <summary>
    /// First-person temple controller with rebindable keyboard and mouse actions.
    /// The cursor release key only unlocks the pointer; attack and interact remain
    /// active while the cursor is visible, as requested.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(CharacterController))]
    public class StarTemplePlayerController : MonoBehaviour
    {
        public const string BindingPrefPrefix = "StarTemple.Binding.";

        public static readonly StarAction[] AllActions =
        {
            StarAction.Forward, StarAction.Back, StarAction.Left, StarAction.Right,
            StarAction.Jump, StarAction.Crouch, StarAction.Sprint, StarAction.Cursor,
            StarAction.Attack, StarAction.Interact
        };

        [Header("References")]
        public Camera playerCamera;
        public LineRenderer attackTrace;

        [Header("Movement")]
        public float walkSpeed = 3.8f;
        public float sprintSpeed = 7.0f;
        public float crouchSpeed = 2.0f;
        public float jumpHeight = 1.15f;
        public float gravity = -22.0f;
        public float mouseSensitivity = 2.2f;
        public float standingHeight = 1.8f;
        public float crouchHeight = 1.05f;
        public float crouchTransitionSpeed = 6.0f;

        [Header("Interaction")]
        public float attackRange = 4.5f;
        public float interactRange = 5.5f;
        public float attackDamage = 10.0f;
        public float attackCooldown = 0.28f;

        public event Action<RaycastHit, float> AttackHit;
        public event Action<RaycastHit> Interacted;

        CharacterController controller;
        Dictionary<StarAction, KeyCode> bindings;
        StarAction? waitingAction;
        bool cursorVisible;
        bool showBindings;
        bool modalFrozen;
        bool cursorBeforeModal;
        bool crouched;
        float pitch;
        float standingCameraHeight = 1.62f;
        float verticalVelocity;
        float nextAttackTime;
        float nextInteractTime;
        float traceUntil;
        int suppressInputUntilFrame = -1;
        Vector3 spawnPosition;
        Quaternion spawnRotation;
        int collisionMask;
        string notice = string.Empty;
        float noticeUntil;
        GUIStyle hudStyle;
        GUIStyle noticeStyle;
        GUIStyle keyStyle;
        GUIStyle titleStyle;
        Font uiFont;
        Vector2 bindingScroll;

        public bool CursorVisible { get { return cursorVisible; } }
        public bool IsCrouched { get { return crouched; } }
        public bool IsKeybindingMenuOpen { get { return showBindings; } }
        public bool IsModalFrozen { get { return modalFrozen; } }
        public IReadOnlyDictionary<StarAction, KeyCode> Bindings { get { return bindings; } }
        StarTempleMobileGamepad MobilePad { get { return StarTempleMobileGamepad.Active ? StarTempleMobileGamepad.Instance : null; } }
        bool MobileUiOwnsInput { get { return MobilePad != null && StarTempleWorldUiController.MobileUiOwnsInputThisFrame; } }

        void Awake()
        {
            if (Application.platform == RuntimePlatform.Android && GetComponent<StarTempleMobileGamepad>() == null)
                gameObject.AddComponent<StarTempleMobileGamepad>();

            controller = GetComponent<CharacterController>();
            if (playerCamera == null) playerCamera = GetComponentInChildren<Camera>(true);
            if (attackTrace == null) attackTrace = GetComponentInChildren<LineRenderer>(true);

            standingHeight = Mathf.Max(1.2f, standingHeight);
            crouchHeight = Mathf.Clamp(crouchHeight, 0.7f, standingHeight - 0.1f);
            if (playerCamera != null)
            {
                standingCameraHeight = playerCamera.transform.localPosition.y;
                pitch = NormalizePitch(playerCamera.transform.localEulerAngles.x);
            }

            spawnPosition = transform.position;
            spawnRotation = transform.rotation;
            collisionMask = ~(1 << gameObject.layer);
            bindings = LoadBindings();
            if (attackTrace != null) attackTrace.enabled = false;
            SetCursorVisible(false);
        }

        void OnDisable()
        {
            if (Application.platform == RuntimePlatform.Android) return;
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
        }

        void OnApplicationFocus(bool focused)
        {
            if (!focused) SetCursorVisible(true);
        }

        void Update()
        {
            // Desktop keeps the original modal freeze. On Android the same panel
            // remains gyro-aimable and allows movement, matching the other UI.
            if (modalFrozen && MobilePad == null) return;
            if (modalFrozen)
            {
                Look();
                UpdateCrouch();
                UpdateMovement();
                return;
            }
            if (Time.frameCount <= suppressInputUntilFrame) return;
            if (waitingAction.HasValue) return;

            if (Input.GetKeyDown(KeyCode.F1))
            {
                ToggleBindings();
                return;
            }

            if (Pressed(StarAction.Cursor)) ToggleCursor();

            if (!cursorVisible && !showBindings) Look();

            UpdateCrouch();
            UpdateMovement();

            bool uiConsumesClick = showBindings || StarTempleWorldUiController.PointerOverUiThisFrame || MobileUiOwnsInput;
            var pad = MobilePad;
            if (!uiConsumesClick && (Pressed(StarAction.Attack) || (pad != null && pad.AttackPressed))) DoAttack();
            if (!uiConsumesClick && (Pressed(StarAction.Interact) || (pad != null && pad.InteractPressed))) DoInteract();

            if (attackTrace != null && attackTrace.enabled && Time.time >= traceUntil)
                attackTrace.enabled = false;
        }

        void Look()
        {
            if (playerCamera == null) return;
            float yawDelta = Input.GetAxisRaw("Mouse X") * mouseSensitivity;
            float pitchDelta = Input.GetAxisRaw("Mouse Y") * mouseSensitivity;
            var pad = MobilePad;
            if (pad != null)
            {
                yawDelta += pad.Look.x * Time.deltaTime;
                pitchDelta += pad.Look.y * Time.deltaTime;
            }
            float yaw = transform.eulerAngles.y + yawDelta;
            pitch = Mathf.Clamp(pitch - pitchDelta, -85f, 85f);
            transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            playerCamera.transform.localRotation = Quaternion.Euler(pitch, 0f, 0f);
        }

        void UpdateCrouch()
        {
            var pad = MobilePad;
            bool wantsCrouch = Held(StarAction.Crouch) || (pad != null && pad.CrouchHeld);
            if (wantsCrouch)
            {
                crouched = true;
            }
            else if (crouched && CanStandUp())
            {
                crouched = false;
            }

            float targetHeight = crouched ? crouchHeight : standingHeight;
            controller.height = Mathf.MoveTowards(controller.height, targetHeight, crouchTransitionSpeed * Time.deltaTime);
            controller.center = new Vector3(0f, controller.height * 0.5f, 0f);

            if (playerCamera != null)
            {
                float targetEye = standingCameraHeight - (standingHeight - controller.height);
                var local = playerCamera.transform.localPosition;
                local.y = Mathf.MoveTowards(local.y, targetEye, crouchTransitionSpeed * 1.1f * Time.deltaTime);
                playerCamera.transform.localPosition = local;
            }
        }

        bool CanStandUp()
        {
            float currentHeight = controller.height;
            float distance = standingHeight - currentHeight + 0.15f;
            if (distance <= 0f) return true;
            Vector3 origin = transform.position + Vector3.up * (currentHeight - controller.radius + 0.06f);
            RaycastHit hit;
            return !Physics.SphereCast(origin, controller.radius * 0.9f, Vector3.up, out hit,
                distance, collisionMask, QueryTriggerInteraction.Ignore);
        }

        void UpdateMovement()
        {
            var pad = MobilePad;
            bool grounded = controller.isGrounded;
            if (grounded)
            {
                if (verticalVelocity < 0f) verticalVelocity = -2f;
                if (Pressed(StarAction.Jump) || (pad != null && pad.JumpPressed && !MobileUiOwnsInput))
                {
                    if (crouched)
                    {
                        if (CanStandUp()) crouched = false;
                        else Notice("头顶空间不足，无法站起");
                    }
                    if (!crouched)
                        verticalVelocity = Mathf.Sqrt(jumpHeight * -2f * gravity);
                }
            }
            else
            {
                verticalVelocity += gravity * Time.deltaTime;
            }

            float horizontal = (Held(StarAction.Right) ? 1f : 0f) - (Held(StarAction.Left) ? 1f : 0f);
            float vertical = (Held(StarAction.Forward) ? 1f : 0f) - (Held(StarAction.Back) ? 1f : 0f);
            if (pad != null)
            {
                horizontal += pad.Move.x;
                vertical += pad.Move.y;
            }
            Vector3 input = transform.right * horizontal + transform.forward * vertical;
            if (input.sqrMagnitude > 1f) input.Normalize();

            bool sprinting = Held(StarAction.Sprint) || (pad != null && pad.SprintHeld);
            float speed = crouched ? crouchSpeed : (sprinting ? sprintSpeed : walkSpeed);
            Vector3 motion = input * speed + Vector3.up * verticalVelocity;
            controller.Move(motion * Time.deltaTime);

            if ((controller.collisionFlags & CollisionFlags.Above) != 0 && verticalVelocity > 0f)
                verticalVelocity = 0f;

            if (transform.position.y < -12f)
            {
                controller.enabled = false;
                transform.position = spawnPosition;
                transform.rotation = spawnRotation;
                controller.enabled = true;
                verticalVelocity = 0f;
                Notice("已返回神殿起点");
            }
        }

        void DoAttack()
        {
            if (Time.time < nextAttackTime) return;
            nextAttackTime = Time.time + attackCooldown;
            if (playerCamera == null) return;

            Ray ray = playerCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            RaycastHit hit;
            Vector3 end = ray.origin + ray.direction * attackRange;
            bool found = Physics.Raycast(ray, out hit, attackRange, collisionMask, QueryTriggerInteraction.Collide);
            if (found)
            {
                end = hit.point;
                var damageable = hit.collider.GetComponentInParent<IStarTempleDamageable>();
                if (damageable != null) damageable.TakeDamage(attackDamage, this, hit);
                if (AttackHit != null) AttackHit(hit, attackDamage);
                Notice(damageable != null ? "攻击命中：" + hit.collider.name : "攻击：命中 " + hit.collider.name);
            }
            else
            {
                Notice("攻击：未命中");
            }

            if (attackTrace != null)
            {
                attackTrace.enabled = true;
                attackTrace.positionCount = 2;
                attackTrace.SetPosition(0, ray.origin + ray.direction * 0.15f);
                attackTrace.SetPosition(1, end);
                traceUntil = Time.time + 0.075f;
            }
        }

        void DoInteract()
        {
            if (Time.time < nextInteractTime) return;
            nextInteractTime = Time.time + 0.15f;
            if (playerCamera == null) return;

            Ray ray = playerCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            RaycastHit hit;
            if (Physics.Raycast(ray, out hit, interactRange, collisionMask, QueryTriggerInteraction.Collide))
            {
                var interactable = hit.collider.GetComponentInParent<IStarTempleInteractable>();
                if (interactable != null)
                {
                    interactable.Interact(this);
                    if (Interacted != null) Interacted(hit);
                }
                else
                {
                    Notice("交互：" + hit.collider.name + " 没有交互功能");
                }
            }
            else
            {
                Notice("交互：未找到目标");
            }
        }

        /// <summary>
        /// Freezes or restores all player input. Opening a modal forces the cursor
        /// visible; closing it restores the previous cursor state.
        /// </summary>
        public void SetModalFrozen(bool frozen)
        {
            if (modalFrozen == frozen) return;
            modalFrozen = frozen;
            verticalVelocity = 0f;
            if (frozen)
            {
                cursorBeforeModal = cursorVisible;
                showBindings = false;
                waitingAction = null;
                SetCursorVisible(true);
            }
            else
            {
                SetCursorVisible(cursorBeforeModal);
            }
        }

        public void ToggleCursor()
        {
            if (modalFrozen) return;
            bool makeVisible = !cursorVisible;
            SetCursorVisible(makeVisible);
            if (!makeVisible) showBindings = false;
        }

        public void ToggleBindings()
        {
            waitingAction = null;
            showBindings = !showBindings;
            if (showBindings) SetCursorVisible(true);
        }

        public void OpenBindings()
        {
            waitingAction = null;
            showBindings = true;
            SetCursorVisible(true);
        }

        public void CloseBindings()
        {
            waitingAction = null;
            showBindings = false;
        }

        void SetCursorVisible(bool visible)
        {
            if (Application.platform == RuntimePlatform.Android)
            {
                cursorVisible = false;
                Cursor.visible = false;
                Cursor.lockState = CursorLockMode.None;
                return;
            }
            cursorVisible = visible;
            Cursor.visible = visible;
            Cursor.lockState = visible ? CursorLockMode.None : CursorLockMode.Locked;
        }

        public void Notice(string message)
        {
            notice = message;
            noticeUntil = Time.time + 1.8f;
        }

        bool Held(StarAction action)
        {
            KeyCode key;
            return bindings != null && bindings.TryGetValue(action, out key) && key != KeyCode.None && Input.GetKey(key);
        }

        bool Pressed(StarAction action)
        {
            KeyCode key;
            return bindings != null && bindings.TryGetValue(action, out key) && key != KeyCode.None && Input.GetKeyDown(key);
        }

        public static Dictionary<StarAction, KeyCode> CreateDefaultBindings()
        {
            return new Dictionary<StarAction, KeyCode>
            {
                { StarAction.Forward, KeyCode.W },
                { StarAction.Back, KeyCode.S },
                { StarAction.Left, KeyCode.A },
                { StarAction.Right, KeyCode.D },
                { StarAction.Jump, KeyCode.Space },
                { StarAction.Crouch, KeyCode.LeftShift },
                { StarAction.Sprint, KeyCode.Q },
                { StarAction.Cursor, KeyCode.E },
                { StarAction.Attack, KeyCode.Mouse0 },
                { StarAction.Interact, KeyCode.Mouse1 }
            };
        }

        public static string DefaultBindingSignature()
        {
            var defaults = CreateDefaultBindings();
            var parts = new List<string>();
            foreach (var action in AllActions) parts.Add(action + "=" + defaults[action]);
            return string.Join(";", parts.ToArray());
        }

        static Dictionary<StarAction, KeyCode> LoadBindings()
        {
            var result = CreateDefaultBindings();
            foreach (var action in AllActions)
            {
                string key = BindingPrefPrefix + action;
                if (!PlayerPrefs.HasKey(key)) continue;
                int raw = PlayerPrefs.GetInt(key, (int)result[action]);
                if (Enum.IsDefined(typeof(KeyCode), raw)) result[action] = (KeyCode)raw;
            }
            return result;
        }

        static void SaveBindings(Dictionary<StarAction, KeyCode> data)
        {
            foreach (var action in AllActions)
            {
                KeyCode key;
                if (data.TryGetValue(action, out key))
                    PlayerPrefs.SetInt(BindingPrefPrefix + action, (int)key);
            }
            PlayerPrefs.Save();
        }

        void AssignBinding(StarAction action, KeyCode key)
        {
            if (key == KeyCode.F1)
            {
                Notice("F1 固定用于打开键位设置");
                return;
            }
            KeyCode previous = bindings[action];
            foreach (var other in AllActions)
            {
                if (other != action && bindings[other] == key) bindings[other] = previous;
            }
            bindings[action] = key;
            SaveBindings(bindings);
            waitingAction = null;
            suppressInputUntilFrame = Time.frameCount + 1;
            Notice("键位已保存：" + ActionLabel(action) + " = " + KeyLabel(key));
        }

        [ContextMenu("恢复默认键位")]
        public void ResetBindings()
        {
            bindings = CreateDefaultBindings();
            SaveBindings(bindings);
            waitingAction = null;
            Notice("已恢复默认键位");
        }

        static float NormalizePitch(float angle)
        {
            if (angle > 180f) angle -= 360f;
            return angle;
        }

        public static string ActionLabel(StarAction action)
        {
            switch (action)
            {
                case StarAction.Forward: return "前进";
                case StarAction.Back: return "后退";
                case StarAction.Left: return "左移";
                case StarAction.Right: return "右移";
                case StarAction.Jump: return "跳跃";
                case StarAction.Crouch: return "蹲下";
                case StarAction.Sprint: return "疾跑";
                case StarAction.Cursor: return "呼出/锁定鼠标";
                case StarAction.Attack: return "左键攻击";
                case StarAction.Interact: return "右键交互";
            }
            return action.ToString();
        }

        public static string KeyLabel(KeyCode key)
        {
            switch (key)
            {
                case KeyCode.Space: return "空格";
                case KeyCode.LeftShift: return "左 Shift";
                case KeyCode.RightShift: return "右 Shift";
                case KeyCode.Mouse0: return "鼠标左键";
                case KeyCode.Mouse1: return "鼠标右键";
                case KeyCode.Mouse2: return "鼠标中键";
                case KeyCode.UpArrow: return "↑";
                case KeyCode.DownArrow: return "↓";
                case KeyCode.LeftArrow: return "←";
                case KeyCode.RightArrow: return "→";
                case KeyCode.Return: return "Enter";
                case KeyCode.Escape: return "Esc";
                case KeyCode.LeftControl: return "左 Ctrl";
                case KeyCode.Tab: return "Tab";
                case KeyCode.None: return "未设置";
            }
            int code = (int)key;
            if (code >= (int)KeyCode.Alpha0 && code <= (int)KeyCode.Alpha9)
                return (code - (int)KeyCode.Alpha0).ToString();
            if (code >= (int)KeyCode.Keypad0 && code <= (int)KeyCode.Keypad9)
                return "小键盘" + (code - (int)KeyCode.Keypad0);
            return key.ToString();
        }

        void CaptureRebindEvent()
        {
            if (!waitingAction.HasValue) return;
            Event e = Event.current;
            if (e == null) return;

            if (e.type == EventType.KeyDown && e.keyCode != KeyCode.None)
            {
                if (e.keyCode == KeyCode.Escape)
                {
                    waitingAction = null;
                    Notice("已取消修改键位");
                }
                else
                {
                    AssignBinding(waitingAction.Value, e.keyCode);
                }
                e.Use();
            }
            else if (e.type == EventType.MouseDown && e.button <= 1)
            {
                AssignBinding(waitingAction.Value, e.button == 0 ? KeyCode.Mouse0 : KeyCode.Mouse1);
                e.Use();
            }
        }

        void EnsureStyles()
        {
            if (hudStyle != null) return;
            hudStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 19,
                normal = { textColor = new Color(1f, 0.87f, 0.64f, 0.92f) }
            };
            noticeStyle = new GUIStyle(hudStyle)
            {
                fontSize = 24,
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold
            };
            titleStyle = new GUIStyle(hudStyle)
            {
                fontSize = 25,
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(1f, 0.78f, 0.40f, 1f) }
            };
            keyStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 18
            };

            uiFont = Font.CreateDynamicFontFromOSFont(
                new[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "Noto Sans CJK SC" }, 18);
            if (uiFont != null)
            {
                hudStyle.font = uiFont;
                noticeStyle.font = uiFont;
                titleStyle.font = uiFont;
                keyStyle.font = uiFont;
                GUI.skin.font = uiFont;
            }
        }

        void OnGUI()
        {
            CaptureRebindEvent();
            EnsureStyles();

            Matrix4x4 previous = GUI.matrix;
            float scale = Mathf.Min(Screen.width / 1920f, Screen.height / 1080f);
            float offsetX = (Screen.width - 1920f * scale) * 0.5f;
            float offsetY = (Screen.height - 1080f * scale) * 0.5f;
            GUI.matrix = Matrix4x4.TRS(new Vector3(offsetX, offsetY, 0f), Quaternion.identity, new Vector3(scale, scale, 1f));
            GUI.depth = -1000;

            Color oldColor = GUI.color;
            GUI.color = new Color(1f, 0.82f, 0.45f, 0.9f);
            GUI.DrawTexture(new Rect(959f, 528f, 2f, 24f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(948f, 539f, 24f, 2f), Texture2D.whiteTexture);
            GUI.color = oldColor;

            GUI.Label(new Rect(24f, 20f, 920f, 38f), "星火神殿 · 第一人称", titleStyle);
            var mobilePad = MobilePad;
            if (mobilePad != null)
            {
                GUI.Label(new Rect(24f, 62f, 1800f, 34f),
                    mobilePad.SingleStickDevice
                        ? "SC-B03 摇杆 移动 · 陀螺仪 视角 · A 跳跃/确认 · X 攻击 · B/Y 交互 · 确定键 疾跑"
                        : "左摇杆 移动 · 陀螺仪 视角 · A 跳跃/确认 · X/RT 攻击 · Y/LT 交互 · LB 疾跑 · B/RB 蹲下", hudStyle);
                GUI.Label(new Rect(24f, 96f, 1800f, 30f),
                    "手柄：" + mobilePad.DeviceName + (mobilePad.GyroAvailable ? " · 陀螺仪已启用" : " · 陀螺仪不可用"), hudStyle);
            }
            else
            {
                GUI.Label(new Rect(24f, 62f, 1500f, 34f),
                    "移动 [" + ShortKey(StarAction.Forward) + ShortKey(StarAction.Left) + ShortKey(StarAction.Back) + ShortKey(StarAction.Right) + "]" +
                    "  跳跃 [" + ShortKey(StarAction.Jump) + "]" +
                    "  蹲下 [" + ShortKey(StarAction.Crouch) + "]" +
                    "  疾跑 [" + ShortKey(StarAction.Sprint) + "]" +
                    "  鼠标 [" + ShortKey(StarAction.Cursor) + "]" +
                    "  攻击 [" + ShortKey(StarAction.Attack) + "]" +
                    "  交互 [" + ShortKey(StarAction.Interact) + "]", hudStyle);
                GUI.Label(new Rect(24f, 96f, 1500f, 30f),
                    cursorVisible ? "鼠标已呼出：左键攻击 / 右键交互仍然有效 · F1 修改键位" : "按 " + ShortKey(StarAction.Cursor) + " 呼出鼠标 · F1 修改键位", hudStyle);
            }

            if (!string.IsNullOrEmpty(notice) && Time.time < noticeUntil)
                GUI.Label(new Rect(360f, 140f, 1200f, 44f), notice, noticeStyle);

            if (cursorVisible && !showBindings && MobilePad == null)
            {
                if (GUI.Button(new Rect(1610f, 24f, 286f, 46f), "键位设置  [F1]", keyStyle))
                    OpenBindings();
            }

            if (showBindings)
            {
                GUI.Window(1977, new Rect(530f, 120f, 860f, 820f), DrawBindingsWindow, "星火神殿 · 键位设置");
            }

            GUI.matrix = previous;
        }

        string ShortKey(StarAction action)
        {
            KeyCode key;
            return bindings != null && bindings.TryGetValue(action, out key) ? KeyLabel(key) : "?";
        }

        void DrawBindingsWindow(int id)
        {
            GUI.Label(new Rect(24f, 34f, 812f, 32f),
                waitingAction.HasValue ? "请按下新的按键或鼠标键；Esc 取消" : "点击右侧按键后重新绑定；相同键位会自动交换",
                hudStyle);

            bindingScroll = GUI.BeginScrollView(new Rect(18f, 78f, 824f, 620f), bindingScroll,
                new Rect(0f, 0f, 790f, AllActions.Length * 54f + 10f));
            for (int i = 0; i < AllActions.Length; i++)
            {
                var action = AllActions[i];
                float y = i * 54f;
                GUI.Label(new Rect(12f, y + 8f, 300f, 36f), ActionLabel(action), hudStyle);
                string text = waitingAction.HasValue && waitingAction.Value == action
                    ? "请按键..."
                    : KeyLabel(bindings[action]);
                if (GUI.Button(new Rect(360f, y + 4f, 390f, 42f), text, keyStyle))
                {
                    waitingAction = action;
                }
            }
            GUI.EndScrollView();

            if (GUI.Button(new Rect(18f, 720f, 250f, 48f), "恢复默认键位", keyStyle))
                ResetBindings();
            if (GUI.Button(new Rect(592f, 720f, 250f, 48f), "关闭 [F1 / E]", keyStyle))
                CloseBindings();
        }
    }

}
