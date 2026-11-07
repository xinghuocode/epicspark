using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SparkCode.Generation
{
    /// <summary>
    /// Builds the whole start scene from code: environment, lighting, camera, menu, spark, atmosphere.
    /// Run from the menu or via: Unity.exe -batchmode -executeMethod SparkCode.Generation.TempleSceneBuilder.Build -quit
    /// </summary>
    public static class TempleSceneBuilder
    {
        [MenuItem("SparkCode/重建开始场景 (Rebuild Start Scene)", false, 10)]
        public static void BuildStartScene()
        {
            Build();
        }

        /// <summary>Batch entry: rebuild the scene and render the offline screenshots in one Unity session.</summary>
        public static void BuildAndCapture()
        {
            Build();
            SceneCapture.CaptureAll();
        }

        public static void Build()
        {
            try
            {
                EditorUtility.DisplayProgressBar("SparkCode", "准备生成场景…", 0.02f);

                int quality = ShaderPreviewTest.FindQuality("High Fidelity");
                QualitySettings.SetQualityLevel(quality, true);

                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                AssetDatabase.DeleteAsset(TempleBuildKit.GeneratedRoot);
                AssetDatabase.Refresh();

                EditorUtility.DisplayProgressBar("SparkCode", "创建共享材质…", 0.08f);
                var root = new GameObject("SparkCodeTemple");
                var kit = new TempleBuildKit(root.transform);

                EditorUtility.DisplayProgressBar("SparkCode", "导入 Blender 神殿…", 0.2f);
                EnvironmentInfo env = BlenderTempleImporter.Build(kit);
                if (env.Root == null) env.Root = root.transform;
                TempleBuildKit.MarkStatic(env.Root.gameObject);
                Debug.Log("[TempleSceneBuilder] environment done. root=" + env.Root.name +
                          " sparkAnchor=" + env.SparkAnchor.position + " cameraTarget=" + env.CameraTarget);

                EditorUtility.DisplayProgressBar("SparkCode", "布置天光、相机与后期…", 0.5f);
                var lighting = LightingBuilder.Build(kit, env);

                EditorUtility.DisplayProgressBar("SparkCode", "构建开始菜单…", 0.62f);
                var menu = StartMenuBuilder.Build(kit, lighting.Camera, lighting.Orbit);

                EditorUtility.DisplayProgressBar("SparkCode", "点燃星火…", 0.78f);
                var spark = SparkBuilder.Build(kit, TemplaySparkPosition(env), menu.Controller);
                if (spark.Root != null)
                {
                    Debug.Log("[TempleSceneBuilder] spark built at " + spark.Root.transform.position);
                }

                EditorUtility.DisplayProgressBar("SparkCode", "添加光柱与尘埃…", 0.88f);
                SparkAtmosphereBuilder.Build(kit, env, spark);

                if (menu.Controller != null)
                {
                    if (menu.Controller.orbit == null) menu.Controller.orbit = lighting.Orbit;
                    if (!menu.Controller.gameObject.activeSelf) menu.Controller.gameObject.SetActive(true);
                }

                EditorUtility.DisplayProgressBar("SparkCode", "保存场景…", 0.94f);
                AssetDatabase.SaveAssets();
                bool saved = EditorSceneManager.SaveScene(scene, TempleLayout.StartScenePath);
                if (!saved)
                {
                    Debug.LogError("[TempleSceneBuilder] Failed to save scene to " + TempleLayout.StartScenePath);
                }

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                LogStats();
                Debug.Log("[TempleSceneBuilder] StartScene generated successfully.");
            }
            catch (System.Exception e)
            {
                Debug.LogError("[TempleSceneBuilder] Build failed: " + e);
                throw;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        static Vector3 TemplaySparkPosition(EnvironmentInfo env)
        {
            if (env.SparkAnchor != null) return env.SparkAnchor.position;
            return TempleLayout.SparkAnchor;
        }

        static void LogStats()
        {
            var filters = Object.FindObjectsOfType<MeshFilter>();
            int tris = 0;
            var meshIds = new HashSet<int>();
            foreach (var mf in filters)
            {
                if (mf.sharedMesh == null || !meshIds.Add(mf.sharedMesh.GetInstanceID())) continue;
                tris += mf.sharedMesh.triangles.Length / 3;
            }
            var renderers = Object.FindObjectsOfType<Renderer>();
            Debug.Log("[TempleSceneBuilder] unique meshes=" + meshIds.Count + " renderers=" + renderers.Length + " triangles=" + tris);
        }
    }
}
