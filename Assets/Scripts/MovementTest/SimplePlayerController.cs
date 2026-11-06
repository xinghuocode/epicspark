using UnityEngine;

namespace EpicSpark.MovementTest
{
    /// <summary>
    /// 移动测试控制器:WASD / 方向键 / 蓝牙手柄摇杆 控制移动,空格 / 手柄确认键 跳跃。
    ///
    /// 输入映射(legacy Input Manager,默认 axes 已覆盖):
    ///   Horizontal / Vertical 轴: A/D、←/→、手柄1 左摇杆 X/Y(本项目蓝牙手柄 js0 的 X/Y 轴自动映射)
    ///   跳跃: 空格,或手柄确认键(本手柄为 JoystickButton5,可在 Inspector 调整)
    ///
    /// 注意:经实测,该蓝牙手柄(VR Shinecon SC-B03)固件只在摇杆活动时上报按键事件,
    /// 因此手柄确认键只有在摇杆活动时才能被检测到(固件行为,非代码问题)。
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class SimplePlayerController : MonoBehaviour
    {
        [Header("移动")]
        public float moveSpeed = 5f;
        public float jumpHeight = 1.2f;
        public float gravity = -18f;
        [Range(0f, 0.5f)] public float stickDeadZone = 0.15f;

        [Header("按键映射")]
        [Tooltip("手柄跳跃键")]
        public KeyCode gamepadJumpButton = KeyCode.JoystickButton0;
        [Tooltip("手柄加速键")]
        public KeyCode gamepadSprintButton = KeyCode.JoystickButton5;
        [Tooltip("加速倍率")]
        public float sprintMultiplier = 1.8f;

        [Header("轴映射")]
        [Tooltip("交换摇杆的 X/Y 轴(某些手柄固件轴序不同,表现为左右/前后颠倒)")]
        public bool swapStickXY = false;
        [Tooltip("反转水平轴")]
        public bool invertX = false;
        [Tooltip("反转垂直轴")]
        public bool invertY = false;

        [Header("调试")]
        public bool showDebugOverlay = true;
        [Tooltip("调试面板额外缩放系数,在自动缩放基础上微调")]
        public float uiScaleMultiplier = 1f;

        private CharacterController _cc;
        private float _verticalVelocity;
        private readonly bool[] _buttonStates = new bool[20];
        private Vector2 _rawInput;

        private void Awake()
        {
            _cc = GetComponent<CharacterController>();
        }

        private void Update()
        {
            // ---- 键盘直读按键;摇杆走 Horizontal/Vertical 轴 ----
            // 键盘与摇杆分开处理,轴映射(交换/反转)只作用于摇杆,不影响键盘。
            // 键盘按下时优先采用键盘输入,避免 Horizontal/Vertical 里的键盘分量被轴映射污染。
            float kx = 0f, ky = 0f;
            if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) kx += 1f;
            if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow)) kx -= 1f;
            if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow)) ky += 1f;
            if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow)) ky -= 1f;
            bool keyboardActive = kx != 0f || ky != 0f;

            // 原始摇杆值(仅用于调试显示)
            _rawInput = new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));

            Vector2 stick;
            if (keyboardActive)
            {
                stick = Vector2.zero; // 键盘优先
            }
            else
            {
                float jx = _rawInput.x;
                float jy = _rawInput.y;

                if (swapStickXY)
                    (jx, jy) = (jy, jx);
                if (invertX) jx = -jx;
                if (invertY) jy = -jy;

                stick = new Vector2(jx, jy);
                if (stick.magnitude < stickDeadZone)
                    stick = Vector2.zero;
            }

            // 合并输入,限幅到 [-1,1]
            Vector2 input = new Vector2(
                Mathf.Clamp(kx + stick.x, -1f, 1f),
                Mathf.Clamp(ky + stick.y, -1f, 1f));
            if (input.sqrMagnitude > 1f)
                input.Normalize();

            // 相机相对移动(本测试场景相机无旋转,等价于世界坐标)
            Transform cam = Camera.main != null ? Camera.main.transform : transform;
            Vector3 forward = Vector3.ProjectOnPlane(cam.forward, Vector3.up).normalized;
            Vector3 right = Vector3.ProjectOnPlane(cam.right, Vector3.up).normalized;
            // ---- 加速 ----
            bool sprinting = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(gamepadSprintButton);
            float speed = moveSpeed * (sprinting ? sprintMultiplier : 1f);
            Vector3 move = (forward * input.y + right * input.x) * speed;

            // ---- 跳跃与重力 ----
            if (_cc.isGrounded)
            {
                _verticalVelocity = -2f;
                if (Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(gamepadJumpButton))
                    _verticalVelocity = Mathf.Sqrt(jumpHeight * -2f * gravity);
            }
            else
            {
                _verticalVelocity += gravity * Time.deltaTime;
            }

            move.y = _verticalVelocity;
            _cc.Move(move * Time.deltaTime);

            // 采样手柄按钮状态(用于调试面板)
            if (showDebugOverlay)
            {
                for (int i = 0; i < _buttonStates.Length; i++)
                    _buttonStates[i] = Input.GetKey((KeyCode)((int)KeyCode.JoystickButton0 + i));
            }
        }

        private void OnGUI()
        {
            if (!showDebugOverlay) return;

            // 以 1080p 为基准自适应缩放,避免高 DPI / 小窗口下面板超出可视区域
            float scale = (Screen.height / 1080f) * Mathf.Clamp(uiScaleMultiplier, 0.2f, 5f);
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));

            GUILayout.BeginArea(new Rect(10, 10, 360, 440), GUI.skin.box);
            GUILayout.Label("<b>移动测试</b> WASD/摇杆=移动 空格/B0=跳跃 Shift/B5=加速");
            GUILayout.Label($"摇杆原始值: X={_rawInput.x:F2}  Y={_rawInput.y:F2}");
            swapStickXY = GUILayout.Toggle(swapStickXY, "交换 X/Y 轴");
            invertX = GUILayout.Toggle(invertX, "反转 X 轴");
            invertY = GUILayout.Toggle(invertY, "反转 Y 轴");
            GUILayout.Label($"位置: {transform.position}   落地: {_cc.isGrounded}");

            string[] names = Input.GetJoystickNames();
            GUILayout.Label($"手柄: {(names.Length == 0 ? "无" : string.Join(", ", names))}");

            GUILayout.Label("手柄按钮:");
            for (int i = 0; i < _buttonStates.Length; i += 5)
            {
                string row = "";
                for (int j = i; j < Mathf.Min(i + 5, _buttonStates.Length); j++)
                    row += $"B{j}{(_buttonStates[j] ? "●" : "○")}  ";
                GUILayout.Label(row);
            }
            GUILayout.EndArea();
            GUI.matrix = Matrix4x4.identity; // 复位,避免影响其他 IMGUI
        }
    }
}
