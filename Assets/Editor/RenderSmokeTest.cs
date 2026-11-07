using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SparkCode.EditorTools
{
    public static class RenderSmokeTest
    {
        public static void Capture()
        {
            EditorSceneManager.OpenScene(SparkCode.Generation.StarTempleEntryScene.ScenePath);
            var camGo = new GameObject("SmokeCam");
            var cam = camGo.AddComponent<Camera>();
            cam.transform.position = new Vector3(0f, 3f, -10f);
            cam.transform.LookAt(new Vector3(0f, 1f, 0f));
            cam.clearFlags = CameraClearFlags.Skybox;

            var rt = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            rt.Create();
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(1280, 720, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
            tex.Apply();
            var bytes = tex.EncodeToPNG();
            Directory.CreateDirectory("Screenshots");
            File.WriteAllBytes("Screenshots/smoke.png", bytes);
            Debug.Log("[SmokeTest] wrote Screenshots/smoke.png bytes=" + bytes.Length);
            RenderTexture.active = null;
            cam.targetTexture = null;
            rt.Release();
            Object.DestroyImmediate(camGo);
        }
    }
}
