using System.Collections.Generic;
using System.Threading.Tasks;
using EpicSpark.AI.SceneAgentTest;
using EpicSpark.AI.StoryAgent;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace EpicSpark.AI.Play.EditorTools
{
    /// <summary>
    /// 菜单 "EpicSpark/AI/生成河姆渡场景资产":
    ///   1. 用 PlaySceneBuilder 在 Editor 下搭建两个预制场景并保存为真实 .unity 文件
    ///      (Assets/Scenes/Hemudu/河姆渡村落.unity, Assets/Scenes/Hemudu/稻作农田.unity);
    ///   2. 创建体验入口场景 Assets/Scenes/EpicSparkPlay.unity (PlayDirector + 相机)。
    /// 异步执行(不阻塞编辑器主线程), 带进度条。之后 Play EpicSparkPlay 体验。
    /// </summary>
    public static class PlayBootstrap
    {
        const string SceneDir = "Assets/Scenes/Hemudu";
        const string PlayScenePath = "Assets/Scenes/EpicSparkPlay.unity";

        [MenuItem("EpicSpark/AI/生成河姆渡场景资产")]
        public static async void BuildHemuduScenes()
        {
            var script = PlayScripts.All[0]; // 河姆渡文化
            var genPaths = new Dictionary<string, string>(); // 模型均为内置资产

            if (!AssetDatabase.IsValidFolder("Assets/Scenes"))
                AssetDatabase.CreateFolder("Assets", "Scenes");
            if (!AssetDatabase.IsValidFolder(SceneDir))
                AssetDatabase.CreateFolder("Assets/Scenes", "Hemudu");

            try
            {
                for (int i = 0; i < script.scenes.Count; i++)
                {
                    var def = script.scenes[i];
                    EditorUtility.DisplayProgressBar("生成河姆渡场景资产",
                        $"正在搭建 {def.sceneName} ({i + 1}/{script.scenes.Count}) …", i / (float)(script.scenes.Count + 1));

                    // 异步搭建(glTFast 续体由编辑器主循环调度, 绝不阻塞)
                    // 每次新建场景前清空 GLB 缓存: 上个场景卸载时 UnloadUnusedAssets 会销毁缓存的 Mesh
                    ModelGltfCache.Clear();
                    var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                    var root = new GameObject("Root");
                    await PlaySceneBuilder.BuildAsync(def, root.transform, genPaths);

                    string path = $"{SceneDir}/{def.sceneName}.unity";
                    if (!EditorSceneManager.SaveScene(scene, path))
                    {
                        Debug.LogError("[PlayBootstrap] 场景保存失败: " + path);
                        return;
                    }
                    Debug.Log("[PlayBootstrap] 已生成场景: " + path);
                }

                EditorUtility.DisplayProgressBar("生成河姆渡场景资产", "创建体验入口场景…", 0.9f);
                var playScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var directorGo = new GameObject("PlayDirector");
                directorGo.AddComponent<PlayDirector>();
                var cameraGo = new GameObject("Main Camera");
                cameraGo.tag = "MainCamera";
                cameraGo.AddComponent<Camera>();
                cameraGo.transform.position = new Vector3(0, 30, -45);
                cameraGo.transform.LookAt(Vector3.zero);
                if (!EditorSceneManager.SaveScene(playScene, PlayScenePath))
                {
                    Debug.LogError("[PlayBootstrap] 场景保存失败: " + PlayScenePath);
                    return;
                }

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                EditorUtility.DisplayDialog("河姆渡场景资产",
                    "已生成:\n" +
                    $"{SceneDir}/{script.scenes[0].sceneName}.unity\n" +
                    $"{SceneDir}/{script.scenes[1].sceneName}.unity\n" +
                    $"{PlayScenePath}\n\n打开 EpicSparkPlay 点 Play 即可体验。", "好的");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }
    }
}
