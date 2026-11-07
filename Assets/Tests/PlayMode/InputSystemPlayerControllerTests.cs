using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.TestTools;

namespace EpicSpark.MovementTest.Tests
{
    /// <summary>
    /// 使用 Input System 虚拟设备验证控制器；不代表实体 SC-B03 的硬件校准结果。
    /// </summary>
    public sealed class InputSystemPlayerControllerTests : InputTestFixture
    {
        private Gamepad _pad;
        private Keyboard _keyboard;
        private GameObject _ground;
        private GameObject _player;
        private GameObject _camera;
        private CharacterController _character;
        private InputSystemPlayerController _controller;

        public override void Setup()
        {
            base.Setup();
            _pad = InputSystem.AddDevice<Gamepad>();
            _keyboard = InputSystem.AddDevice<Keyboard>();
            Time.captureFramerate = 60;
        }

        public override void TearDown()
        {
            Time.captureFramerate = 0;
            DestroyWorld();
            base.TearDown();
        }

        private void CreateWorld(bool useScB03Defaults = false)
        {
            _ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            _ground.name = "TestGround";
            _ground.transform.localScale = new Vector3(4f, 1f, 4f);

            _player = new GameObject("TestPlayer");
            _player.transform.position = new Vector3(0f, 0.1f, 0f);
            _character = _player.AddComponent<CharacterController>();
            _character.center = new Vector3(0f, 0.9f, 0f);
            _character.height = 1.8f;
            _character.radius = 0.35f;

            _controller = _player.AddComponent<InputSystemPlayerController>();
            _controller.showDebugOverlay = false;
            _controller.swapStickXY = useScB03Defaults;
            _controller.invertX = false;
            _controller.invertY = useScB03Defaults;
            _controller.jumpButtonIndex = ButtonIndex(_pad.buttonSouth);
            _controller.sprintButtonIndex = ButtonIndex(_pad.buttonEast);

            _camera = new GameObject("TestMainCamera");
            _camera.tag = "MainCamera";
            _camera.transform.position = new Vector3(0f, 2f, -8f);
            _camera.AddComponent<Camera>();
        }

        private void DestroyWorld()
        {
            if (_camera != null) UnityEngine.Object.DestroyImmediate(_camera);
            if (_player != null) UnityEngine.Object.DestroyImmediate(_player);
            if (_ground != null) UnityEngine.Object.DestroyImmediate(_ground);
            _camera = null;
            _player = null;
            _ground = null;
        }

        private int ButtonIndex(ButtonControl target)
        {
            List<ButtonControl> buttons = _pad.allControls
                .OfType<ButtonControl>()
                .OrderBy(control => control.path, StringComparer.Ordinal)
                .ToList();
            int index = buttons.FindIndex(control => ReferenceEquals(control, target));
            Assert.That(index, Is.GreaterThanOrEqualTo(0), "测试按钮必须存在于排序后的控件列表中");
            return index;
        }

        private static IEnumerator Frames(int count)
        {
            for (int i = 0; i < count; i++)
                yield return null;
        }

        private IEnumerator ResetPlayer()
        {
            Set(_pad.leftStick, Vector2.zero);
            Release(_pad.buttonEast);
            Release(_pad.buttonSouth);
            _controller.enabled = false;
            _character.enabled = false;
            _player.transform.position = new Vector3(0f, 0.1f, 0f);
            _character.enabled = true;
            _controller.enabled = true;
            yield return Frames(8);
        }

        private float HorizontalDistanceFromStart()
        {
            Vector3 position = _player.transform.position;
            return new Vector2(position.x, position.z).magnitude;
        }

        [UnityTest]
        public IEnumerator StandardMapping_RightAndUpMoveInExpectedDirections()
        {
            CreateWorld();
            yield return Frames(8);

            Set(_pad.leftStick, Vector2.right);
            yield return Frames(12);
            Set(_pad.leftStick, Vector2.zero);
            Assert.That(_player.transform.position.x, Is.GreaterThan(0.3f), "标准映射：右摇杆应向右移动");
            Assert.That(Mathf.Abs(_player.transform.position.z), Is.LessThan(0.05f));

            yield return ResetPlayer();
            Set(_pad.leftStick, Vector2.up);
            yield return Frames(12);
            Set(_pad.leftStick, Vector2.zero);
            Assert.That(_player.transform.position.z, Is.GreaterThan(0.3f), "标准映射：上摇杆应向前移动");
            Assert.That(Mathf.Abs(_player.transform.position.x), Is.LessThan(0.05f));
        }

        [UnityTest]
        public IEnumerator ScB03Defaults_SwapAndInvertOnlyGamepadAxes()
        {
            CreateWorld(useScB03Defaults: true);
            yield return Frames(8);

            // 原始右 (1,0) -> 交换后 (0,1) -> Y 反转后 (0,-1)，即向后。
            Set(_pad.leftStick, Vector2.right);
            yield return Frames(12);
            Set(_pad.leftStick, Vector2.zero);
            Assert.That(_player.transform.position.z, Is.LessThan(-0.3f), "SC-B03 默认映射应交换并反转 Y");
            Assert.That(Mathf.Abs(_player.transform.position.x), Is.LessThan(0.05f));

            yield return ResetPlayer();
            Press(_keyboard.dKey);
            Set(_pad.leftStick, Vector2.up);
            yield return Frames(12);
            Set(_pad.leftStick, Vector2.zero);
            Release(_keyboard.dKey);
            Assert.That(_player.transform.position.x, Is.GreaterThan(0.3f), "键盘优先且不能受手柄轴映射影响");
            Assert.That(Mathf.Abs(_player.transform.position.z), Is.LessThan(0.05f));
        }

        [UnityTest]
        public IEnumerator DeadZone_SmallStickInputDoesNotMovePlayer()
        {
            CreateWorld();
            yield return Frames(8);
            Vector3 start = _player.transform.position;

            Set(_pad.leftStick, new Vector2(0.1f, 0.1f));
            yield return Frames(15);
            Set(_pad.leftStick, Vector2.zero);

            Vector3 delta = _player.transform.position - start;
            Assert.That(new Vector2(delta.x, delta.z).magnitude, Is.LessThan(0.02f));
        }

        [UnityTest]
        public IEnumerator SprintButton_MultipliesMovementSpeed()
        {
            CreateWorld();
            yield return Frames(8);

            Set(_pad.leftStick, Vector2.right);
            yield return Frames(12);
            Set(_pad.leftStick, Vector2.zero);
            float normalDistance = HorizontalDistanceFromStart();

            yield return ResetPlayer();
            Press(_pad.buttonEast);
            Set(_pad.leftStick, Vector2.right);
            yield return Frames(12);
            Set(_pad.leftStick, Vector2.zero);
            Release(_pad.buttonEast);
            float sprintDistance = HorizontalDistanceFromStart();

            Assert.That(normalDistance, Is.GreaterThan(0.3f));
            Assert.That(sprintDistance / normalDistance,
                Is.EqualTo(_controller.sprintMultiplier).Within(0.08f));
        }

        [UnityTest]
        public IEnumerator JumpButton_JumpsAndReturnsToGround()
        {
            CreateWorld();
            yield return Frames(8);
            float startY = _player.transform.position.y;
            float peakY = startY;

            Press(_pad.buttonSouth);
            yield return null;
            Release(_pad.buttonSouth);
            for (int i = 0; i < 70; i++)
            {
                peakY = Mathf.Max(peakY, _player.transform.position.y);
                yield return null;
            }

            Assert.That(peakY - startY, Is.GreaterThan(0.6f), "跳跃按钮应让角色离开地面");
            Assert.That(_character.isGrounded, Is.True, "跳跃后应重新落地");
        }

        [UnityTest]
        public IEnumerator HeldJumpButton_DoesNotRetriggerJumpAfterLanding()
        {
            CreateWorld();
            yield return Frames(8);

            Press(_pad.buttonSouth);
            bool wasGrounded = _character.isGrounded;
            bool landedAfterFirstJump = false;
            bool jumpedAgainWhileHeld = false;

            for (int i = 0; i < 120; i++)
            {
                yield return null;
                bool grounded = _character.isGrounded;
                if (!wasGrounded && grounded)
                    landedAfterFirstJump = true;
                if (landedAfterFirstJump && !grounded)
                {
                    jumpedAgainWhileHeld = true;
                    break;
                }
                wasGrounded = grounded;
            }

            Release(_pad.buttonSouth);
            Assert.That(landedAfterFirstJump, Is.True, "第一次跳跃应已完成并落地");
            Assert.That(jumpedAgainWhileHeld, Is.False,
                "跳跃应采用按下边沿；按住按钮落地后不应自动再次跳跃");
        }

        [UnityTest]
        public IEnumerator YButtonIndices_AnyListedButtonCountsAsPressed()
        {
            CreateWorld();
            _controller.yButtonIndices = new[]
            {
                ButtonIndex(_pad.buttonNorth),
                ButtonIndex(_pad.buttonWest)
            };
            yield return null;

            Press(_pad.buttonNorth);
            yield return null;
            Assert.That(_controller.ReadYButton(_pad), Is.True);
            Release(_pad.buttonNorth);
            yield return null;

            Press(_pad.buttonWest);
            yield return null;
            Assert.That(_controller.ReadYButton(_pad), Is.True);
            Release(_pad.buttonWest);
            yield return null;
            Assert.That(_controller.ReadYButton(_pad), Is.False);
        }
    }
}
