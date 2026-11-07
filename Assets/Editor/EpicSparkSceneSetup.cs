// 编辑器工具：生成 EpicSpark 主场景，并可被命令行 batchmode 调用。
// 菜单：EpicSpark/生成主场景
// 命令行：Unity.exe -batchmode -quit -projectPath <工程> -executeMethod EpicSpark.EditorTools.EpicSparkSceneSetup.BuildAndSave -logFile <日志>
using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using EpicSpark.Bootstrap;

namespace EpicSpark.EditorTools
{
    public static class EpicSparkSceneSetup
    {
        public const string ScenePath = "Assets/Scenes/EpicSpark.unity";

        [MenuItem("EpicSpark/打开主场景（EpicSpark.unity）", false, 1)]
        public static void OpenMainScene()
        {
            string path = "Assets/Scenes/EpicSpark.unity";
            if (System.IO.File.Exists(path))
            {
                UnityEditor.SceneManagement.EditorSceneManager.OpenScene(path);
                Debug.Log("[EpicSpark] 已打开主场景，按 Play 即可开始。");
                return;
            }
            Debug.LogError("[EpicSpark] 找不到 " + path + "，先执行「EpicSpark/生成主场景」。");
        }
        [MenuItem("EpicSpark/生成主场景")]
        public static void BuildAndSave()
        {
            try
            {
                Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

                // 相机（Bootstrap 会在需要时接管为主视角）
                var cameraGo = new GameObject("Main Camera");
                var camera = cameraGo.AddComponent<Camera>();
                camera.tag = "MainCamera";
                camera.clearFlags = CameraClearFlags.Skybox;
                camera.nearClipPlane = 0.05f;
                camera.farClipPlane = 1200f;
                cameraGo.transform.position = new Vector3(0f, 1.6f, -4f);
                if (UnityEngine.Object.FindObjectOfType<AudioListener>() == null)
                    cameraGo.AddComponent<AudioListener>();

                // 方向光
                var lightGo = new GameObject("Directional Light");
                var light = lightGo.AddComponent<Light>();
                light.type = LightType.Directional;
                light.color = new Color(1f, 0.96f, 0.88f);
                light.intensity = 1.15f;
                light.shadows = LightShadows.Soft;
                lightGo.transform.rotation = Quaternion.Euler(48f, 35f, 0f);

                // 启动器
                var bootstrapGo = new GameObject("EpicSpark");
                var bootstrap = bootstrapGo.AddComponent<GameBootstrap>();
                bootstrap.defaultEvent = "法国大革命";
                bootstrap.baseUrl = "https://ai.gitee.com/v1";
                bootstrap.chatModel = "Qwen3-32B";
                bootstrap.initialTolerance = 0.5f;

                EnsureFolder("Assets/Scenes");
                bool saved = EditorSceneManager.SaveScene(scene, ScenePath);
                if (!saved) Debug.LogError("[EpicSpark] 场景保存失败: " + ScenePath);
                else Debug.Log("[EpicSpark] 场景已保存: " + ScenePath);

                AddSceneToBuildSettings(ScenePath);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                Debug.Log("[EpicSpark] 主场景构建完成");
            }
            catch (Exception e)
            {
                Debug.LogError("[EpicSpark] 主场景构建失败: " + e);
                throw;
            }
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = System.IO.Path.GetDirectoryName(path).Replace('\\', '/');
            string leaf = System.IO.Path.GetFileName(path);
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }

        private static void AddSceneToBuildSettings(string scenePath)
        {
            var scenes = new System.Collections.Generic.List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            for (int i = 0; i < scenes.Count; i++)
            {
                if (scenes[i].path == scenePath)
                {
                    scenes[i] = new EditorBuildSettingsScene(scenePath, true);
                    EditorBuildSettings.scenes = scenes.ToArray();
                    return;
                }
            }
            scenes.Insert(0, new EditorBuildSettingsScene(scenePath, true));
            EditorBuildSettings.scenes = scenes.ToArray();
            Debug.Log("[EpicSpark] 已加入 Build Settings: " + scenePath);
        }

        [MenuItem("EpicSpark/打开存档目录")]
        public static void OpenSaveFolder()
        {
            SaveService.OpenSaveFolder();
        }

        [MenuItem("EpicSpark/验证工程资产")]
        public static void VerifyAssets()
        {
            try
            {
                string modelsDir = System.IO.Path.Combine(Application.streamingAssetsPath, "Models");
                int glbCount = 0;
                if (System.IO.Directory.Exists(modelsDir))
                    glbCount = System.IO.Directory.GetFiles(modelsDir, "*.glb").Length;
                string library = System.IO.Path.Combine(modelsDir, "model_library.json");
                bool hasLibrary = System.IO.File.Exists(library);
                string corpus = System.IO.Path.Combine(Application.dataPath, "Resources/Knowledge/Events");
                int corpusFiles = System.IO.Directory.Exists(corpus)
                    ? System.IO.Directory.GetFiles(corpus, "*.txt").Length : 0;

                Debug.Log("[EpicSpark] 资产核验: GLB=" + glbCount + " 模型库=" + hasLibrary
                          + " 语料文件=" + corpusFiles);
                if (glbCount < 43) Debug.LogWarning("[EpicSpark] GLB 数量少于 43，请确认 StreamingAssets/Models 完整");
                if (!hasLibrary) Debug.LogError("[EpicSpark] 缺少 model_library.json，搭建 Agent 无法检索");
                if (corpusFiles < 26) Debug.LogWarning("[EpicSpark] 语料文件少于 26，知识库覆盖不足");
            }
            catch (Exception e)
            {
                Debug.LogError("[EpicSpark] 资产核验失败: " + e);
            }
        }
    }
}
