using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;

namespace EpicSpark.MovementTest.Tests
{
    /// <summary>Virtual-device tests for Android input mapping; not a hardware gyro test.</summary>
    public sealed class MobileGamepadMappingTests : InputTestFixture
    {
        private Gamepad pad;
        private GameObject host;
        private object inputComponent;
        private Type inputType;

        public override void Setup()
        {
            base.Setup();
            pad = InputSystem.AddDevice<Gamepad>();
            inputType = Type.GetType("SparkCode.Gameplay.StarTempleMobileGamepad, Assembly-CSharp");
            Assert.That(inputType, Is.Not.Null, "手机手柄输入类型必须存在");
            host = new GameObject("MobileGamepadMappingTests");
            inputComponent = host.AddComponent(inputType);
        }

        public override void TearDown()
        {
            if (host != null) UnityEngine.Object.DestroyImmediate(host);
            host = null;
            inputComponent = null;
            base.TearDown();
        }

        [Test]
        public void StandardStick_MovesAndHonorsDeadZone()
        {
            Set(pad.leftStick, Vector2.right);
            Poll();
            Assert.That(Get<Vector2>("Move").x, Is.GreaterThan(0.95f));
            Assert.That(Get<Vector2>("Move").y, Is.Zero);

            Set(pad.leftStick, new Vector2(0.05f, 0f));
            Poll();
            Assert.That(Get<Vector2>("Move"), Is.EqualTo(Vector2.zero));
        }

        [Test]
        public void StandardButtons_MapToGameplayActions()
        {
            Press(pad.buttonSouth);
            Poll();
            Assert.That(Get<bool>("JumpPressed"), Is.True);
            Assert.That(Get<bool>("ConfirmPressed"), Is.True);

            Release(pad.buttonSouth);
            Press(pad.rightTrigger);
            Poll();
            Assert.That(Get<bool>("AttackPressed"), Is.True);

            Release(pad.rightTrigger);
            Press(pad.buttonNorth);
            Poll();
            Assert.That(Get<bool>("InteractPressed"), Is.True);
        }

        [Test]
        public void ScB03Axes_SwapAndInvertAsDocumented()
        {
            Vector2 mapped = ScB03Axes(new Vector2(0.75f, 0.25f));
            Assert.That(mapped.x, Is.EqualTo(0.25f).Within(0.0001f));
            Assert.That(mapped.y, Is.EqualTo(-0.75f).Within(0.0001f));
        }

#if UNITY_EDITOR
        [Test]
        public void AndroidSettings_AreLandscapeArm64AndIl2Cpp()
        {
            Assert.That(UnityEditor.PlayerSettings.defaultInterfaceOrientation,
                Is.EqualTo(UnityEditor.UIOrientation.LandscapeLeft));
            Assert.That(UnityEditor.PlayerSettings.Android.targetArchitectures,
                Is.EqualTo(UnityEditor.AndroidArchitecture.ARM64));
            Assert.That(UnityEditor.PlayerSettings.GetScriptingBackend(UnityEditor.Build.NamedBuildTarget.Android),
                Is.EqualTo(UnityEditor.ScriptingImplementation.IL2CPP));
        }
#endif

        private void Poll()
        {
            inputType.GetMethod("PollInput", BindingFlags.Public | BindingFlags.Instance)
                .Invoke(inputComponent, null);
        }

        private T Get<T>(string propertyName)
        {
            return (T)inputType.GetProperty(propertyName).GetValue(inputComponent, null);
        }

        private Vector2 ScB03Axes(Vector2 value)
        {
            return (Vector2)inputType.GetMethod("ScB03Axes", BindingFlags.Public | BindingFlags.Static)
                .Invoke(null, new object[] { value });
        }
    }

}
