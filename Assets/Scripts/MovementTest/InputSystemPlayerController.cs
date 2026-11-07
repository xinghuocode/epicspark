#if ENABLE_INPUT_SYSTEM
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace EpicSpark.MovementTest
{
    /// <summary>
    /// 基于 Input System 包的移动控制器(替代 legacy 版)。
    ///
    /// 与 legacy 版的区别:
    ///   - 直接读取 Keyboard / Gamepad / Joystick 设备,不经过 InputManager 轴表
    ///   - 轴映射(交换/反转)是设备级的,真正的"动态修改映射",运行时勾选即时生效
    ///   - 手柄按键按索引读取(B0=跳跃 B5=加速),适配 SC-B03 这类非标准手柄
    ///
    /// 键位:键盘 WASD/方向键=移动 空格=跳跃 左Shift=加速
    ///       手柄 摇杆=移动 B0=跳跃 B5=加速
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class InputSystemPlayerController : MonoBehaviour
    {
        [Header("移动")]
        public float moveSpeed = 5f;
        public float jumpHeight = 1.2f;
        public float gravity = -18f;
        [Range(0f, 0.5f)] public float stickDeadZone = 0.15f;
        public float sprintMultiplier = 1.8f;

        [Header("手柄按键映射(按钮索引)")]
        [Tooltip("跳跃按钮索引,SC-B03 校准后为 19")]
        public int jumpButtonIndex = 19;
        [Tooltip("加速按钮索引,SC-B03 校准后为 17")]
        public int sprintButtonIndex = 17;
        [Tooltip("Y 键索引列表(物理 Y 键会同时触发其中任意一个,命中其一即算按下)")]
        public int[] yButtonIndices = { 15, 21 };

        [Header("轴映射(仅手柄,运行时生效)")]
        [Tooltip("SC-B03 固件轴序反置,默认开启")]
        public bool swapStickXY = true;
        public bool invertX = false;
        [Tooltip("SC-B03 固件Y轴反向,默认开启")]
        public bool invertY = true;

        [Header("调试")]
        public bool showDebugOverlay = true;
        public float uiScaleMultiplier = 1f;

        private CharacterController _cc;
        private float _verticalVelocity;
        private Vector2 _rawStick;
        private string _deviceNames = "";

        private void Awake()
        {
            _cc = GetComponent<CharacterController>();
        }

        /// <summary>当前使用的手柄设备:优先标准 Gamepad,否则任意 Joystick</summary>
        private static InputDevice CurrentGamepad
        {
            get
            {
                if (Gamepad.current != null) return Gamepad.current;
                var joysticks = Joystick.all;
                for (int i = 0; i < joysticks.Count; i++)
                    if (joysticks[i] != null) return joysticks[i];
                return null;
            }
        }

        private void Update()
        {
            // ---- 键盘 ----
            var kb = Keyboard.current;
            Vector2 keyInput = Vector2.zero;
            if (kb != null)
            {
                if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) keyInput.x -= 1f;
                if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) keyInput.x += 1f;
                if (kb.wKey.isPressed || kb.upArrowKey.isPressed) keyInput.y += 1f;
                if (kb.sKey.isPressed || kb.downArrowKey.isPressed) keyInput.y -= 1f;
            }

            // ---- 手柄摇杆(原始值 → 轴映射 → 死区) ----
            var pad = CurrentGamepad;
            _rawStick = Vector2.zero;
            if (pad != null)
            {
                if (pad is Gamepad g)
                    _rawStick = g.leftStick.ReadValue();
                else if (pad is Joystick j)
                    _rawStick = j.stick.ReadValue();
            }

            float jx = _rawStick.x, jy = _rawStick.y;
            if (swapStickXY) (jx, jy) = (jy, jx);
            if (invertX) jx = -jx;
            if (invertY) jy = -jy;

            var stick = new Vector2(jx, jy);
            if (stick.magnitude < stickDeadZone)
                stick = Vector2.zero;

            // 键盘按下时键盘优先,轴映射不影响键盘
            if (keyInput != Vector2.zero)
                stick = Vector2.zero;

            Vector2 input = keyInput + stick;
            if (input.sqrMagnitude > 1f) input.Normalize();

            // ---- 跳跃 / 加速 ----
            bool jump = (kb != null && kb.spaceKey.wasPressedThisFrame)
                        || ReadButton(pad, jumpButtonIndex, wasPressedThisFrame: true);
            bool sprint = (kb != null && kb.leftShiftKey.isPressed)
                          || ReadButton(pad, sprintButtonIndex);

            // ---- 相机相对移动 ----
            Transform cam = Camera.main != null ? Camera.main.transform : transform;
            Vector3 forward = Vector3.ProjectOnPlane(cam.forward, Vector3.up).normalized;
            Vector3 right = Vector3.ProjectOnPlane(cam.right, Vector3.up).normalized;
            float speed = moveSpeed * (sprint ? sprintMultiplier : 1f);
            Vector3 move = (forward * input.y + right * input.x) * speed;

            // ---- 重力 ----
            if (_cc.isGrounded)
            {
                _verticalVelocity = -2f;
                if (jump)
                    _verticalVelocity = Mathf.Sqrt(jumpHeight * -2f * gravity);
            }
            else
            {
                _verticalVelocity += gravity * Time.deltaTime;
            }

            move.y = _verticalVelocity;
            _cc.Move(move * Time.deltaTime);

            // ---- 设备名(调试用,低频更新) ----
            if ((Time.frameCount & 63) == 0)
            {
                var names = new System.Collections.Generic.List<string>();
                var gps = Gamepad.all;
                for (int i = 0; i < gps.Count; i++) names.Add("GP:" + gps[i].displayName);
                var js = Joystick.all;
                for (int i = 0; i < js.Count; i++) names.Add("JS:" + js[i].displayName);
                _deviceNames = names.Count == 0 ? "无" : string.Join(", ", names);
            }
        }

        /// <summary>
        /// 设备按钮的枚举缓存(按 control path 排序,索引稳定)。
        /// jumpButtonIndex / sprintButtonIndex 就是在这个列表里的下标。
        /// </summary>
        private System.Collections.Generic.List<ButtonControl> _buttons = new System.Collections.Generic.List<ButtonControl>();
        private InputDevice _buttonsCachedFor;

        private void EnsureButtonsCached(InputDevice dev)
        {
            if (dev == _buttonsCachedFor) return;
            _buttonsCachedFor = dev;
            _buttons.Clear();
            if (dev == null) return;
            foreach (var c in dev.allControls)
            {
                if (c is ButtonControl b && b != null)
                    _buttons.Add(b);
            }
            _buttons.Sort((a, b2) => string.CompareOrdinal(a.path, b2.path));
        }

        /// <summary>按枚举列表索引读按钮；跳跃使用按下边沿，加速使用持续按住状态。</summary>
        private bool ReadButton(InputDevice dev, int index, bool wasPressedThisFrame = false)
        {
            if (dev == null || index < 0) return false;
            EnsureButtonsCached(dev);
            if (index >= _buttons.Count) return false;
            return wasPressedThisFrame
                ? _buttons[index].wasPressedThisFrame
                : _buttons[index].isPressed;
        }

        /// <summary>Y 键:索引列表中任意一个触发即算按下(固件一个物理键映射多个控件)</summary>
        public bool ReadYButton(InputDevice dev)
        {
            if (dev == null || yButtonIndices == null) return false;
            for (int i = 0; i < yButtonIndices.Length; i++)
            {
                if (ReadButton(dev, yButtonIndices[i]))
                    return true;
            }
            return false;
        }

        /// <summary>按钮诊断信息:索引:名字(按下状态),供映射校对</summary>
        private string DescribeButton(InputDevice dev, int index)
        {
            if (dev == null || index < 0) return $"{index}:?";
            EnsureButtonsCached(dev);
            if (index >= _buttons.Count) return $"{index}:?";
            var b = _buttons[index];
            string shortName = b.name ?? b.path;
            return $"{index}:{shortName}{(b.isPressed ? "●" : "○")}";
        }

        private void OnGUI()
        {
            if (!showDebugOverlay) return;

            float scale = (Screen.height / 1080f) * Mathf.Clamp(uiScaleMultiplier, 0.2f, 5f);
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));

            GUILayout.BeginArea(new Rect(10, 10, 420, 620), GUI.skin.box);
            GUILayout.Label("<b>移动测试 (Input System)</b> WASD/摇杆=移动 空格/B0=跳跃 Shift/B5=加速");
            GUILayout.Label($"摇杆原始值: X={_rawStick.x:F2}  Y={_rawStick.y:F2}");
            swapStickXY = GUILayout.Toggle(swapStickXY, "交换 X/Y 轴");
            invertX = GUILayout.Toggle(invertX, "反转 X 轴");
            invertY = GUILayout.Toggle(invertY, "反转 Y 轴");
            GUILayout.Label($"位置: {transform.position}   落地: {_cc.isGrounded}");
            GUILayout.Label($"设备: {_deviceNames}");

            var pad = CurrentGamepad;
            EnsureButtonsCached(pad);
            GUILayout.Label($"手柄按钮(共{_buttons.Count}个,跳跃=B{jumpButtonIndex} 加速=B{sprintButtonIndex}):");
            GUILayout.Label($"Y 键: {(ReadYButton(pad) ? "● 按下" : "○ 松开")} (索引 {string.Join(" 或 ", yButtonIndices)})");
            for (int i = 0; i < _buttons.Count && i < 32; i += 2)
            {
                string row = DescribeButton(pad, i);
                if (i + 1 < _buttons.Count) row += "   " + DescribeButton(pad, i + 1);
                GUILayout.Label(row);
            }
            GUILayout.EndArea();
            GUI.matrix = Matrix4x4.identity;
        }
    }
}
#endif
