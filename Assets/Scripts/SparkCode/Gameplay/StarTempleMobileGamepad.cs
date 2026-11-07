using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace SparkCode.Gameplay
{
    /// <summary>
    /// Android-only physical gamepad input. The left stick moves the player and the
    /// phone gyroscope controls the camera. Desktop keyboard/mouse input is unchanged.
    /// </summary>
    [DefaultExecutionOrder(-200)]
    public sealed class StarTempleMobileGamepad : MonoBehaviour
    {
        public static StarTempleMobileGamepad Instance { get; private set; }
        public static bool Active
        {
            get { return Instance != null && Instance.isActiveAndEnabled && Application.platform == RuntimePlatform.Android; }
        }

        [Header("Movement")]
        [Range(0f, 0.5f)] public float stickDeadZone = 0.18f;

        [Header("Gyroscope (landscape left)")]
        public float gyroSensitivity = 1.0f;
        public float gyroDeadZone = 0.025f;
        public bool swapGyroAxes = true;
        public bool invertGyroYaw = true;
        public bool invertGyroPitch;

        public Vector2 Move { get; private set; }
        public Vector2 Look { get; private set; }
        public bool JumpPressed { get; private set; }
        public bool ConfirmPressed { get; private set; }
        public bool AttackPressed { get; private set; }
        public bool InteractPressed { get; private set; }
        public bool CancelPressed { get; private set; }
        public bool CrouchHeld { get; private set; }
        public bool SprintHeld { get; private set; }
        public bool NavigateUp { get; private set; }
        public bool NavigateDown { get; private set; }
        public bool NavigateLeft { get; private set; }
        public bool NavigateRight { get; private set; }
        public bool NavigateNext { get { return NavigateDown || NavigateRight; } }
        public bool NavigatePrevious { get { return NavigateUp || NavigateLeft; } }
        public string DeviceName { get; private set; } = "未连接";
        public bool SingleStickDevice { get; private set; }
        public bool GyroAvailable { get; private set; }

        InputDevice device;
        readonly List<ButtonControl> indexedButtons = new List<ButtonControl>();
        InputDevice buttonsCachedFor;
        int navigationDirection;
        bool gyroEnabledByUs;

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }
            Instance = this;
            if (Application.platform != RuntimePlatform.Android) return;

            Screen.orientation = ScreenOrientation.LandscapeLeft;
            GyroAvailable = SystemInfo.supportsGyroscope;
            if (GyroAvailable) EnableGyro();
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (gyroEnabledByUs)
            {
                Input.gyro.enabled = false;
                gyroEnabledByUs = false;
            }
        }

        void OnApplicationPause(bool paused)
        {
            if (Application.platform != RuntimePlatform.Android || !GyroAvailable) return;
            if (paused && gyroEnabledByUs) Input.gyro.enabled = false;
            else if (!paused) EnableGyro();
        }

        void EnableGyro()
        {
            Input.gyro.updateInterval = 1f / 60f;
            Input.gyro.enabled = true;
            gyroEnabledByUs = true;
        }

        void Update()
        {
            if (!Active)
            {
                ResetFrame();
                return;
            }
            PollInput();
        }

        /// <summary>Reads the current devices once. Public to allow deterministic input tests.</summary>
        public void PollInput()
        {
            ResetFrame();
            var gamepad = Gamepad.current;
            if (gamepad != null)
            {
                device = gamepad;
                ReadStandardGamepad(gamepad);
            }
            else
            {
                var joystick = FirstJoystick();
                device = joystick;
                if (joystick != null) ReadJoystick(joystick);
                else DeviceName = "未连接";
            }

            if (Active) ReadGyroscope();
        }

        void ReadStandardGamepad(Gamepad pad)
        {
            DeviceName = string.IsNullOrEmpty(pad.displayName) ? pad.name : pad.displayName;
            SingleStickDevice = IsScB03(DeviceName);
            Vector2 leftStick = ApplyDeadZone(pad.leftStick.ReadValue(), stickDeadZone);
            if (SingleStickDevice) leftStick = ApplyDeadZone(ScB03Axes(pad.leftStick.ReadValue()), stickDeadZone);
            Move = leftStick;

            JumpPressed = pad.buttonSouth.wasPressedThisFrame;
            ConfirmPressed = JumpPressed;
            AttackPressed = pad.buttonWest.wasPressedThisFrame || pad.rightTrigger.wasPressedThisFrame;
            InteractPressed = pad.buttonNorth.wasPressedThisFrame || pad.leftTrigger.wasPressedThisFrame;
            if (SingleStickDevice) InteractPressed |= pad.buttonEast.wasPressedThisFrame;
            CrouchHeld = pad.selectButton.isPressed || pad.rightShoulder.isPressed;
            SprintHeld = pad.startButton.isPressed || pad.leftShoulder.isPressed || pad.leftStickButton.isPressed;
            CancelPressed = pad.buttonEast.wasPressedThisFrame || pad.selectButton.wasPressedThisFrame ||
                            pad.startButton.wasPressedThisFrame;
            UpdateNavigation(leftStick, pad.dpad);
        }

        void ReadJoystick(Joystick joystick)
        {
            DeviceName = string.IsNullOrEmpty(joystick.displayName) ? joystick.name : joystick.displayName;
            SingleStickDevice = IsScB03(DeviceName);
            Vector2 stick = joystick.stick.ReadValue();
            if (SingleStickDevice) stick = ScB03Axes(stick);
            Move = ApplyDeadZone(stick, stickDeadZone);
            EnsureButtonsCached(joystick);

            if (SingleStickDevice)
            {
                JumpPressed = ReadButton(19, true);
                ConfirmPressed = JumpPressed;
                AttackPressed = ReadButton(16, true);
                InteractPressed = ReadButton(20, true) || ReadButton(15, true) || ReadButton(21, true);
                SprintHeld = ReadButton(17, false);
                CancelPressed = ReadButton(20, true);
            }
            else
            {
                JumpPressed = ReadButton(0, true);
                ConfirmPressed = JumpPressed;
                AttackPressed = ReadButton(2, true);
                InteractPressed = ReadButton(3, true);
                SprintHeld = ReadButton(4, false);
                CrouchHeld = ReadButton(1, false);
                CancelPressed = ReadButton(1, true);
            }
            UpdateNavigation(Move, null);
        }

        void ReadGyroscope()
        {
            GyroAvailable = SystemInfo.supportsGyroscope;
            if (!GyroAvailable || !Input.gyro.enabled)
            {
                Look = Vector2.zero;
                return;
            }

            Vector3 rate = Input.gyro.rotationRateUnbiased;
            if (Mathf.Abs(rate.x) < gyroDeadZone) rate.x = 0f;
            if (Mathf.Abs(rate.y) < gyroDeadZone) rate.y = 0f;

            // Device axes differ between Android sensor implementations. On the
            // test phone, landscape-left yaw/pitch requires the X/Y swap.
            float yawRate = swapGyroAxes ? rate.y : rate.x;
            float pitchRate = swapGyroAxes ? rate.x : rate.y;
            float yaw = yawRate * Mathf.Rad2Deg * gyroSensitivity;
            float pitch = pitchRate * Mathf.Rad2Deg * gyroSensitivity;
            if (invertGyroYaw) yaw = -yaw;
            if (invertGyroPitch) pitch = -pitch;
            Look = new Vector2(yaw, pitch);
        }

        void UpdateNavigation(Vector2 stick, DpadControl dpad)
        {
            int direction = 0;
            if (dpad != null)
            {
                if (dpad.up.isPressed) direction = 1;
                else if (dpad.down.isPressed) direction = 2;
                else if (dpad.left.isPressed) direction = 3;
                else if (dpad.right.isPressed) direction = 4;
            }
            if (direction == 0 && stick.magnitude >= 0.65f)
            {
                if (Mathf.Abs(stick.x) > Mathf.Abs(stick.y)) direction = stick.x < 0f ? 3 : 4;
                else direction = stick.y > 0f ? 1 : 2;
            }

            if (direction != 0 && direction != navigationDirection)
            {
                NavigateUp = direction == 1;
                NavigateDown = direction == 2;
                NavigateLeft = direction == 3;
                NavigateRight = direction == 4;
            }
            navigationDirection = direction;
        }

        static Joystick FirstJoystick()
        {
            var joysticks = Joystick.all;
            return joysticks.Count > 0 ? joysticks[0] : null;
        }

        static bool IsScB03(string name)
        {
            return !string.IsNullOrEmpty(name) &&
                   (name.IndexOf("SC-B03", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Shinecon", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        public static Vector2 ScB03Axes(Vector2 raw)
        {
            return new Vector2(raw.y, -raw.x);
        }

        public static Vector2 ApplyDeadZone(Vector2 value)
        {
            return ApplyDeadZone(value, 0.18f);
        }

        public static Vector2 ApplyDeadZone(Vector2 value, float deadZone)
        {
            float magnitude = value.magnitude;
            if (magnitude <= deadZone || magnitude <= 0.0001f) return Vector2.zero;
            if (magnitude >= 1f) return value.normalized;
            return value * ((magnitude - deadZone) / (1f - deadZone) / magnitude);
        }

        void EnsureButtonsCached(InputDevice inputDevice)
        {
            if (buttonsCachedFor == inputDevice) return;
            buttonsCachedFor = inputDevice;
            indexedButtons.Clear();
            if (inputDevice == null) return;
            foreach (var control in inputDevice.allControls)
            {
                var button = control as ButtonControl;
                if (button != null) indexedButtons.Add(button);
            }
            indexedButtons.Sort((a, b) => string.CompareOrdinal(a.path, b.path));
        }

        bool ReadButton(int index, bool pressedThisFrame)
        {
            if (index < 0 || index >= indexedButtons.Count) return false;
            return pressedThisFrame ? indexedButtons[index].wasPressedThisFrame : indexedButtons[index].isPressed;
        }

        void ResetFrame()
        {
            Move = Vector2.zero;
            Look = Vector2.zero;
            JumpPressed = false;
            ConfirmPressed = false;
            AttackPressed = false;
            InteractPressed = false;
            CancelPressed = false;
            CrouchHeld = false;
            SprintHeld = false;
            NavigateUp = false;
            NavigateDown = false;
            NavigateLeft = false;
            NavigateRight = false;
        }
    }
}
