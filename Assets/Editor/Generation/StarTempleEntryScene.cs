using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SparkCode.Generation
{
    /// <summary>
    /// Keeps both Editor Play Mode and player builds on the one supported entry scene.
    /// Development/showcase scenes remain usable as assets, but are not build entries.
    /// </summary>
    [InitializeOnLoad]
    public static class StarTempleEntryScene
    {
        public const string ScenePath = "Assets/Scenes/BlenderStarTemple_Gameplay.unity";

        static StarTempleEntryScene()
        {
            EditorApplication.delayCall += ConfigurePlayModeStartScene;
        }

        [MenuItem("SparkCode/设置并进入第一人称玩法场景", false, 53)]
        public static void ConfigureAndOpen()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Exit Play Mode before changing the entry scene.");
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            Configure();
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            AssetDatabase.SaveAssets();
            Debug.Log("[StarTempleEntry] Only build scene and Editor Play Mode start scene: " + ScenePath);
        }

        public static void Configure()
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) == null)
                throw new InvalidOperationException("Entry scene missing: " + ScenePath);

            EditorBuildSettings.scenes = new[]
            {
                new EditorBuildSettingsScene(ScenePath, true)
            };
            ConfigurePlayModeStartScene();
        }

        static void ConfigurePlayModeStartScene()
        {
            var scene = AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath);
            if (scene == null)
            {
                Debug.LogWarning("[StarTempleEntry] Entry scene is not imported yet: " + ScenePath);
                return;
            }

            EditorSceneManager.playModeStartScene = scene;
        }
    }
}
