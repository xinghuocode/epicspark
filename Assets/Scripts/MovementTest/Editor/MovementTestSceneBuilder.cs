using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace EpicSpark.MovementTest.EditorTools
{
    /// <summary>
    /// 一键构建移动测试场景:地面 + 玩家胶囊 + 主相机 + 平行光。
    /// 菜单:Tools → Movement Test → 构建移动测试场景
    /// </summary>
    public static class MovementTestSceneBuilder
    {
        private const string ScenePath = "Assets/Scenes/MovementTest.unity";

        [MenuItem("Tools/Movement Test/构建移动测试场景")]
        public static void Build()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // ---- 地面 ----
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.localScale = new Vector3(4f, 1f, 4f); // 40m x 40m
            var groundRenderer = ground.GetComponent<Renderer>();
            var mat = AssetDatabase.LoadAssetAtPath<Material>("Assets/Scenes/MovementTest_Ground.mat");
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit")) { color = new Color(0.35f, 0.55f, 0.35f) };
                AssetDatabase.CreateAsset(mat, "Assets/Scenes/MovementTest_Ground.mat");
            }
            groundRenderer.sharedMaterial = mat;

            // ---- 玩家 ----
            var player = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            player.name = "Player";
            Object.DestroyImmediate(player.GetComponent<CapsuleCollider>());
            var cc = player.AddComponent<CharacterController>();
            cc.center = new Vector3(0f, 0.9f, 0f);
            cc.height = 1.8f;
            cc.radius = 0.35f;
            player.AddComponent<SimplePlayerController>();
            player.transform.position = new Vector3(0f, 0.1f, 0f);

            // ---- 主相机(挂在玩家身上) ----
            var cameraGo = new GameObject("Main Camera");
            cameraGo.tag = "MainCamera";
            var cam = cameraGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.Skybox;
            cameraGo.AddComponent<AudioListener>();
            cameraGo.transform.SetParent(player.transform, false);
            cameraGo.transform.localPosition = new Vector3(0f, 1.6f, 0f); // 第一人称视角

            // ---- 平行光 ----
            var lightGo = new GameObject("Directional Light");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = Color.white;
            light.intensity = 1.2f;
            lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            // ---- 保存开发测试场景；不要污染游戏发布入口 ----
            EditorSceneManager.SaveScene(scene, ScenePath);
            Selection.activeGameObject = player;
            EditorGUIUtility.PingObject(player);
            Debug.Log($"[MovementTest] 场景已构建并保存到 {ScenePath}。点击 Play 后 WASD/摇杆移动,空格/手柄确认键跳跃。");
        }
    }
}
