using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace EpicSpark.AI.SceneAgentTest.EditorTools
{
    /// <summary>
    /// 场景Agent测试场景的引导脚本。
    /// 菜单 "EpicSpark/AI/创建场景Agent测试场景":
    ///   新建并保存测试场景 Assets/Scenes/SceneAgentTest.unity
    ///   (含 SceneAgentTestPanel 左侧面板 / 相机 / 平行光)。
    /// 模型端点信息、系统提示词等在 Play 后通过面板输入, 无需任何 Inspector 配置。
    /// </summary>
    public static class SceneAgentTestBootstrap
    {
        const string ScenePath = "Assets/Scenes/SceneAgentTest.unity";

        [MenuItem("EpicSpark/AI/创建场景Agent测试场景")]
        public static void CreateTestScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // 左侧面板: 运行时输入模型信息与提示词, 无需在 Inspector 中配置
            var panelGo = new GameObject("SceneAgentTestPanel");
            panelGo.AddComponent<SceneAgentTestPanel>();

            var cameraGo = new GameObject("Main Camera");
            cameraGo.tag = "MainCamera";
            cameraGo.AddComponent<Camera>();
            cameraGo.transform.position = new Vector3(0, 5, -10);

            var lightGo = new GameObject("Directional Light");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;

            bool saved = EditorSceneManager.SaveScene(scene, ScenePath);
            if (saved)
            {
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                EditorUtility.DisplayDialog("场景Agent测试",
                    $"测试场景已创建: {ScenePath}\n\n点 Play 后直接在左侧面板中填写 API Key、模型名、系统提示词与 generate_scene 参数, 然后点击 \"运行测试\"。",
                    "好的");
            }
            else
            {
                EditorUtility.DisplayDialog("场景Agent测试", "场景保存失败, 请检查 " + ScenePath, "好的");
            }
        }
    }
}
