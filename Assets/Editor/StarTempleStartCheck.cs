// 星火神殿开始场景（BlenderStarTemple_Gameplay 移植）检查与出图工具（仅编辑器）。
//  - EpicSpark/检查星火神殿开始场景：统计缺失脚本 / 材质 / 相机 / 世界 UI 引用
//  - EpicSpark/截图星火神殿：渲染玩家相机视角、星火史诗菜单、记忆卡到 Logs/startemple-*.png
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace EpicSpark.EditorTools
{
    public static class StarTempleStartCheck
    {
        private const string ScenePath = "Assets/Scenes/StarTempleStart.unity";

        [MenuItem("EpicSpark/检查星火神殿开始场景")]
        public static void Check()
        {
            Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            int missingScripts = 0;
            int renderers = 0;
            int missingMaterials = 0;

            foreach (GameObject root in scene.GetRootGameObjects())
                Scan(root.transform, ref missingScripts, ref renderers, ref missingMaterials);

            Camera[] cameras = Object.FindObjectsOfType<Camera>();
            Light[] lights = Object.FindObjectsOfType<Light>();
            var worldUi = Object.FindObjectOfType<SparkCode.Gameplay.StarTempleWorldUiController>();
            var player = Object.FindObjectOfType<SparkCode.Gameplay.StarTemplePlayerController>();
            var showcase = Object.FindObjectOfType<SparkCode.TempleShowcaseController>();

            Debug.Log("[星火神殿检查] 场景=" + scene.name
                + " 相机=" + cameras.Length + " 灯光=" + lights.Length
                + " Renderer=" + renderers
                + " 缺失脚本=" + missingScripts
                + " 缺失材质=" + missingMaterials);

            if (worldUi == null) Debug.LogError("[星火神殿检查] StarTempleWorldUiController 缺失！");
            else
            {
                Debug.Log("[星火神殿检查] WorldUI player=" + (worldUi.player != null ? worldUi.player.name : "null")
                    + " camera=" + (worldUi.playerCamera != null ? worldUi.playerCamera.name : "null")
                    + " bridge=" + (worldUi.bridgeCanvas != null ? worldUi.bridgeCanvas.name : "null")
                    + " lore=" + (worldUi.loreCanvas != null ? worldUi.loreCanvas.name : "null")
                    + " exit=" + (worldUi.exitCanvas != null ? worldUi.exitCanvas.name : "null")
                    + " font=" + (worldUi.uiFont != null ? worldUi.uiFont.name : "null")
                    + " buttons=" + (worldUi.buttons != null ? worldUi.buttons.Count : 0));
                if (worldUi.bridgeCanvas == null) Debug.LogError("[星火神殿检查] bridgeCanvas 未绑定");
                if (worldUi.playerCamera == null) Debug.LogError("[星火神殿检查] playerCamera 未绑定");
            }

            if (player == null) Debug.LogError("[星火神殿检查] StarTemplePlayerController 缺失！");
            else Debug.Log("[星火神殿检查] Player pos=" + player.transform.position
                + " camera=" + (player.playerCamera != null ? player.playerCamera.name : "null"));

            if (showcase == null) Debug.LogWarning("[星火神殿检查] TempleShowcaseController 缺失（星火不会旋转/浮动）");
            else Debug.Log("[星火神殿检查] Showcase starPivot=" + (showcase.starPivot != null ? showcase.starPivot.name : "null")
                + " cameras=" + (showcase.cameras != null ? showcase.cameras.Length : 0));
        }

        private static void Scan(Transform t, ref int missingScripts, ref int renderers, ref int missingMaterials)
        {
            GameObject go = t.gameObject;
            Component[] comps = go.GetComponents<Component>();
            for (int i = 0; i < comps.Length; i++)
                if (comps[i] == null) missingScripts++;

            var renderer = go.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderers++;
                Material[] mats = renderer.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                    if (mats[i] == null) missingMaterials++;
            }

            for (int i = 0; i < t.childCount; i++)
                Scan(t.GetChild(i), ref missingScripts, ref renderers, ref missingMaterials);
        }

        [MenuItem("EpicSpark/截图星火神殿")]
        public static void CaptureAll()
        {
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            Capture("scene", 0, false, false);
            Capture("menu", 0, false, false);
            Capture("lore", 0, true, false);
        }

        /// <summary>批量检查 + 出图（batchmode -executeMethod 用）。</summary>
        public static void CheckAndCapture()
        {
            Check();
            CaptureAll();
        }

        private static void Capture(string mode, int page, bool showLore, bool showExit)
        {
            var worldUi = Object.FindObjectOfType<SparkCode.Gameplay.StarTempleWorldUiController>();
            var player = Object.FindObjectOfType<SparkCode.Gameplay.StarTemplePlayerController>();
            Camera cam = player != null && player.playerCamera != null
                ? player.playerCamera
                : Object.FindObjectOfType<Camera>();
            if (cam == null)
            {
                Debug.LogError("[星火神殿截图] 场景里没有玩家相机");
                return;
            }

            Vector3 camLocalPos = cam.transform.localPosition;
            Quaternion camLocalRot = cam.transform.localRotation;

            if (worldUi != null)
            {
                if (mode == "scene")
                {
                    worldUi.EditorPreview(-1, false, false);
                    Canvas.ForceUpdateCanvases();
                }
                else if (mode == "lore")
                {
                    // 站到星火近处仰视，还原源工程「记忆卡对焦」的构图。
                    Vector3 star = worldUi.starPivot != null ? worldUi.starPivot.position : new Vector3(0f, 5.75f, 0f);
                    cam.transform.position = new Vector3(0.6f, 3.0f, 4.6f);
                    cam.transform.rotation = Quaternion.LookRotation((star - cam.transform.position).normalized, Vector3.up);
                    worldUi.EditorPreview(-1, false, false);
                    worldUi.EditorPreviewLoreFocus();
                    Canvas.ForceUpdateCanvases();
                }
                else
                {
                    worldUi.EditorPreview(mode == "menu" ? page : -1, showLore, showExit);
                    Canvas.ForceUpdateCanvases();
                }
            }

            const int width = 1600;
            const int height = 1200;
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            RenderTexture previous = RenderTexture.active;
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            tex.Apply();
            byte[] png = tex.EncodeToPNG();
            string path = "Logs/startemple-" + mode + ".png";
            Directory.CreateDirectory("Logs");
            File.WriteAllBytes(path, png);
            cam.targetTexture = null;
            RenderTexture.active = previous;
            Object.DestroyImmediate(rt);
            Object.DestroyImmediate(tex);
            Debug.Log("[星火神殿截图] 已保存 " + Path.GetFullPath(path) + " (" + png.Length + " bytes)");

            cam.transform.localPosition = camLocalPos;
            cam.transform.localRotation = camLocalRot;

            if (worldUi != null)
            {
                // 复位，避免影响后续截图 / 保存场景。
                worldUi.EditorPreview(0, false, false);
                if (worldUi.loreCanvas != null) worldUi.loreCanvas.SetActive(false);
                if (worldUi.exitCanvas != null) worldUi.exitCanvas.SetActive(false);
            }
        }
    }
}
