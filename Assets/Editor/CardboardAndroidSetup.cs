using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.Management;

namespace SparkCode.EditorTools
{
    [InitializeOnLoad]
    public static class CardboardAndroidSetup
    {
        private const string AppliedKey = "SparkCode.CardboardAndroidSetup.V1";
        private const string MainTemplate = "Assets/Plugins/Android/mainTemplate.gradle";
        private const string PropertiesTemplate = "Assets/Plugins/Android/gradleTemplate.properties";

        static CardboardAndroidSetup()
        {
            if (SessionState.GetBool(AppliedKey, false)) return;
            SessionState.SetBool(AppliedKey, true);
            EditorApplication.delayCall += Apply;
        }

        [MenuItem("SparkCode/Android/配置 Cardboard 测试环境")]
        public static void Apply()
        {
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel26;
            PlayerSettings.Android.optimizedFramePacing = false;
            SetProjectSettingBool("ForceInternetPermission", true);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.OpenGLES3 });

            SetProjectSettingBool("useCustomMainGradleTemplate", true);
            SetProjectSettingBool("useCustomGradlePropertiesTemplate", true);
            WriteGradleTemplates();
            ConfigureCardboardLoader();
            AssetDatabase.SaveAssets();
            Debug.Log("[Cardboard] Android setup applied: OpenGLES3, API26+, IL2CPP/ARM64, landscape, QR dependencies.");
        }

        static void SetProjectSettingBool(string propertyName, bool value)
        {
            var settingsAsset = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset")[0];
            var serialized = new SerializedObject(settingsAsset);
            var property = serialized.FindProperty(propertyName);
            if (property == null)
            {
                Debug.LogError("[Cardboard] Project setting not found: " + propertyName);
                return;
            }
            property.boolValue = value;
            serialized.ApplyModifiedProperties();
        }

        static XRGeneralSettingsPerBuildTarget GetOrCreateXrSettingsContainer()
        {
            XRGeneralSettingsPerBuildTarget container = null;
            string[] assets = AssetDatabase.FindAssets("t:XRGeneralSettingsPerBuildTarget");
            if (assets.Length > 0)
            {
                string path = AssetDatabase.GUIDToAssetPath(assets[0]);
                container = AssetDatabase.LoadAssetAtPath<XRGeneralSettingsPerBuildTarget>(path);
            }
            if (container == null)
            {
                Directory.CreateDirectory("Assets/XR/Settings");
                container = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
                AssetDatabase.CreateAsset(container, "Assets/XR/Settings/XRGeneralSettingsPerBuildTarget.asset");
                EditorBuildSettings.AddConfigObject(XRGeneralSettings.k_SettingsKey, container, true);
            }
            if (!container.HasSettingsForBuildTarget(BuildTargetGroup.Android))
                container.CreateDefaultSettingsForBuildTarget(BuildTargetGroup.Android);
            if (!container.HasManagerSettingsForBuildTarget(BuildTargetGroup.Android))
                container.CreateDefaultManagerSettingsForBuildTarget(BuildTargetGroup.Android);
            return container;
        }

        static void ConfigureCardboardLoader()
        {
            var container = GetOrCreateXrSettingsContainer();
            var settings = container.SettingsForBuildTarget(BuildTargetGroup.Android);
            if (settings == null)
            {
                Debug.LogError("[Cardboard] Android XRGeneralSettings could not be created");
                return;
            }
            settings.InitManagerOnStart = false;
            if (settings.AssignedSettings == null)
                settings.AssignedSettings = ScriptableObject.CreateInstance<XRManagerSettings>();
            settings.AssignedSettings.automaticLoading = false;
            settings.AssignedSettings.automaticRunning = false;

            if (!XRPackageMetadataStore.AssignLoader(
                    settings.AssignedSettings, "Google.XR.Cardboard.XRLoader", BuildTargetGroup.Android))
                Debug.LogError("[Cardboard] Failed to assign Google.XR.Cardboard.XRLoader for Android");
            EditorUtility.SetDirty(settings.AssignedSettings);
            EditorUtility.SetDirty(settings);
        }

        static void WriteGradleTemplates()
        {
            Directory.CreateDirectory("Assets/Plugins/Android");
            string templateRoot = Path.Combine(EditorApplication.applicationContentsPath,
                "PlaybackEngines/AndroidPlayer/Tools/GradleTemplates");

            if (!File.Exists(MainTemplate))
                File.Copy(Path.Combine(templateRoot, "mainTemplate.gradle"), MainTemplate);
            if (!File.Exists(PropertiesTemplate))
                File.Copy(Path.Combine(templateRoot, "gradleTemplate.properties"), PropertiesTemplate);

            string main = File.ReadAllText(MainTemplate);
            if (!main.Contains("com.google.android.gms:play-services-vision"))
            {
                string dependencies =
                    "    implementation 'androidx.appcompat:appcompat:1.6.1'\n" +
                    "    implementation 'com.google.android.gms:play-services-vision:20.1.3'\n" +
                    "    implementation 'com.google.android.material:material:1.12.0'\n" +
                    "    implementation 'com.google.protobuf:protobuf-javalite:3.19.4'\n";
                if (!main.Contains("**DEPS**}"))
                    Debug.LogError("[Cardboard] Unexpected mainTemplate.gradle format; add dependencies manually");
                else
                    main = main.Replace("**DEPS**}", dependencies + "**DEPS**}");
                File.WriteAllText(MainTemplate, main);
            }

            string properties = File.ReadAllText(PropertiesTemplate);
            if (!properties.Contains("android.useAndroidX=true")) properties += "\nandroid.useAndroidX=true\n";
            if (!properties.Contains("android.enableJetifier=true")) properties += "android.enableJetifier=true\n";
            File.WriteAllText(PropertiesTemplate, properties);
        }
    }
}
