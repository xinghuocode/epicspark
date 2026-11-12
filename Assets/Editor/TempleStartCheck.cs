// 神殿开始场景移植检查工具（仅编辑器）。
//  - EpicSpark/检查神殿开始场景：打开 StartScene，统计缺失脚本 / 材质 / 相机 / 菜单引用
//  - EpicSpark/截图神殿开始场景：把场景相机渲染到 Logs/temple-start.png
//  - EpicSpark/截图神殿菜单：把开始菜单 Canvas 临时改为 ScreenSpaceCamera 后一起渲染到 Logs/temple-menu.png
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace EpicSpark.EditorTools
{
    public static class TempleStartCheck
    {
        private const string ScenePath = "Assets/Scenes/StartScene.unity";

        [MenuItem("EpicSpark/检查神殿开始场景")]
        public static void Check()
        {
            Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            int missingScripts = 0;
            int renderers = 0;
            int missingMaterials = 0;
            int imagesWithoutSprite = 0;
            var notes = new List<string>();

            foreach (GameObject root in scene.GetRootGameObjects())
                Scan(root.transform, ref missingScripts, ref renderers, ref missingMaterials, ref imagesWithoutSprite);

            Camera[] cameras = Object.FindObjectsOfType<Camera>();
            Light[] lights = Object.FindObjectsOfType<Light>();
            var menu = Object.FindObjectOfType<SparkCode.StartMenuController>();
            var orbit = Object.FindObjectOfType<SparkCode.OrbitCamera>();

            Debug.Log("[神殿检查] 场景=" + scene.name
                + " 相机=" + cameras.Length + " 灯光=" + lights.Length
                + " Renderer=" + renderers
                + " 缺失脚本=" + missingScripts
                + " 缺失材质=" + missingMaterials
                + " 无图 Image=" + imagesWithoutSprite);

            if (menu == null)
            {
                notes.Add("StartMenuController 缺失！");
            }
            else
            {
                if (menu.menuGroup == null) notes.Add("menuGroup 未绑定");
                if (menu.captionGroup == null) notes.Add("captionGroup 未绑定");
                if (menu.mainButtons == null || menu.mainButtons.Length == 0) notes.Add("mainButtons 未绑定");
                notes.Add("menuGroup=" + (menu.menuGroup != null ? menu.menuGroup.name : "null")
                          + " captionGroup=" + (menu.captionGroup != null ? menu.captionGroup.name : "null")
                          + " buttons=" + (menu.mainButtons != null ? menu.mainButtons.Length : 0)
                          + " rows=" + (menu.settingsRows != null ? menu.settingsRows.Length : 0));
            }
            if (orbit == null) notes.Add("OrbitCamera 缺失");
            else notes.Add("OrbitCamera target=" + (orbit.target != null ? orbit.target.name : "null"));

            for (int i = 0; i < notes.Count; i++) Debug.Log("[神殿检查] " + notes[i]);
        }

        private static void Scan(Transform t, ref int missingScripts, ref int renderers,
            ref int missingMaterials, ref int imagesWithoutSprite)
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

            var image = go.GetComponent<Image>();
            if (image != null && image.sprite == null && go.GetComponent<RawImage>() == null)
                imagesWithoutSprite++;

            for (int i = 0; i < t.childCount; i++)
                Scan(t.GetChild(i), ref missingScripts, ref renderers, ref missingMaterials, ref imagesWithoutSprite);
        }

        [MenuItem("EpicSpark/截图神殿开始场景")]
        public static void CaptureTemple()
        {
            Capture(null);
        }

        /// <summary>批量检查 + 出图（batchmode -executeMethod 用）。</summary>
        public static void CheckAndCapture()
        {
            Check();
            CaptureTemple();
            CaptureMenu();
            CaptureAbout();
        }

        [MenuItem("EpicSpark/截图神殿菜单")]
        public static void CaptureMenu()
        {
            Capture("menu");
        }

        [MenuItem("EpicSpark/截图神殿关于面板")]
        public static void CaptureAbout()
        {
            Capture("about");
        }

        private static void Capture(string mode)
        {
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            Camera cam = Camera.main;
            if (cam == null) cam = Object.FindObjectOfType<Camera>();
            if (cam == null)
            {
                Debug.LogError("[神殿截图] 场景里没有相机");
                return;
            }

            if (mode == "menu" || mode == "about")
            {
                Canvas[] canvases = Object.FindObjectsOfType<Canvas>();
                for (int i = 0; i < canvases.Length; i++)
                {
                    Canvas canvas = canvases[i];
                    if (canvas.renderMode != RenderMode.ScreenSpaceOverlay) continue;
                    canvas.renderMode = RenderMode.ScreenSpaceCamera;
                    canvas.worldCamera = cam;
                    canvas.planeDistance = 1f;
                }
            }

            if (mode == "about")
            {
                var menu = Object.FindObjectOfType<SparkCode.StartMenuController>();
                if (menu != null) menu.ShowAbout();
                Canvas.ForceUpdateCanvases();
            }

            const int width = 1920;
            const int height = 1080;
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            RenderTexture previous = RenderTexture.active;
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            tex.Apply();
            byte[] png = tex.EncodeToPNG();
            string path = mode == "menu" ? "Logs/temple-menu.png"
                : (mode == "about" ? "Logs/temple-about.png" : "Logs/temple-start.png");
            Directory.CreateDirectory("Logs");
            File.WriteAllBytes(path, png);
            cam.targetTexture = null;
            RenderTexture.active = previous;
            Object.DestroyImmediate(rt);
            Object.DestroyImmediate(tex);

            Debug.Log("[神殿截图] 已保存 " + Path.GetFullPath(path) + " (" + png.Length + " bytes)");
        }
    }
}
