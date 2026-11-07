using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace SparkCode.EditorTools
{
    [InitializeOnLoad]
    public static class AndroidArm64Apply
    {
        private const string AppliedKey = "SparkCode.AndroidArm64Apply.LandscapeGyroV1";

        static AndroidArm64Apply()
        {
            if (SessionState.GetBool(AppliedKey, false)) return;
            SessionState.SetBool(AppliedKey, true);
            EditorApplication.delayCall += Apply;
        }

        [MenuItem("SparkCode/Android/配置横屏 + IL2CPP + ARM64")]
        public static void Apply()
        {
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
            AssetDatabase.SaveAssets();
            Debug.Log("[AndroidBuild] Android orientation=LandscapeLeft, scripting backend=IL2CPP, target architectures=ARM64");
        }
    }
}
