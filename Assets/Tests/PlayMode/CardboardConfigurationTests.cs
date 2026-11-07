using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEngine;
using UnityEngine.Rendering;

namespace EpicSpark.MovementTest.Tests
{
    public sealed class CardboardConfigurationTests
    {
#if UNITY_EDITOR
        [Test]
        public void CardboardLoaderAndAndroidBuildSettings_AreConfigured()
        {
            Assert.That(XRPackageMetadataStore.IsLoaderAssigned(
                "Google.XR.Cardboard.XRLoader", BuildTargetGroup.Android), Is.True,
                "Android 必须启用 Cardboard XR Loader");
            Assert.That(PlayerSettings.defaultInterfaceOrientation,
                Is.EqualTo(UIOrientation.LandscapeLeft));
            Assert.That(PlayerSettings.Android.targetArchitectures,
                Is.EqualTo(AndroidArchitecture.ARM64));
            Assert.That(PlayerSettings.GetScriptingBackend(UnityEditor.Build.NamedBuildTarget.Android),
                Is.EqualTo(ScriptingImplementation.IL2CPP));
            Assert.That((int)PlayerSettings.Android.minSdkVersion,
                Is.GreaterThanOrEqualTo((int)AndroidSdkVersions.AndroidApiLevel26));
            Assert.That(PlayerSettings.Android.optimizedFramePacing, Is.False);
            Assert.That(PlayerSettings.Android.forceInternetPermission, Is.True);
            Assert.That(PlayerSettings.GetGraphicsAPIs(BuildTarget.Android)[0],
                Is.EqualTo(GraphicsDeviceType.OpenGLES3));
        }

        [Test]
        public void CardboardQrGradleDependencies_ArePresent()
        {
            const string path = "Assets/Plugins/Android/mainTemplate.gradle";
            Assert.That(File.Exists(path), Is.True);
            string gradle = File.ReadAllText(path);
            Assert.That(gradle, Does.Contain("androidx.appcompat:appcompat:1.6.1"));
            Assert.That(gradle, Does.Contain("com.google.android.gms:play-services-vision:20.1.3"));
            Assert.That(gradle, Does.Contain("com.google.android.material:material:1.12.0"));
            Assert.That(gradle, Does.Contain("com.google.protobuf:protobuf-javalite:3.19.4"));

            string properties = File.ReadAllText("Assets/Plugins/Android/gradleTemplate.properties");
            Assert.That(properties, Does.Contain("android.useAndroidX=true"));
            Assert.That(properties, Does.Contain("android.enableJetifier=true"));
        }
#endif
    }
}
