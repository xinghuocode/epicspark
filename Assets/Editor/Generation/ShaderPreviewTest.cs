using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace SparkCode.Generation
{
    /// <summary>Quick offline validation of the custom shaders / materials / post pipeline.</summary>
    public static class ShaderPreviewTest
    {
        public static void Run()
        {
            AssetDatabase.DeleteAsset("Assets/Generated");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            QualitySettings.SetQualityLevel(FindQuality("High Fidelity"), true);

            var root = new GameObject("Preview");
            var kit = new TempleBuildKit(root.transform);

            var sky = kit.CreateMaterial("Sky_Preview", "SparkCode/SkyGradient");
            if (sky != null)
            {
                sky.SetColor("_ZenithColor", new Color(0.12f, 0.25f, 0.48f));
                sky.SetColor("_HorizonColor", new Color(0.74f, 0.79f, 0.83f));
                sky.SetColor("_SunColor", new Color(1f, 0.88f, 0.68f));
                sky.SetColor("_HazeColor", new Color(0.9f, 0.86f, 0.78f));
                sky.SetVector("_SunDirection", new Vector4(0.3f, 0.8f, 0.4f, 0f));
                RenderSettings.skybox = sky;
            }
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.24f, 0.3f, 0.4f);
            RenderSettings.ambientEquatorColor = new Color(0.34f, 0.31f, 0.27f);
            RenderSettings.ambientGroundColor = new Color(0.1f, 0.09f, 0.08f);

            var sunGo = new GameObject("Sun");
            var sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.color = new Color(1f, 0.94f, 0.84f);
            sun.intensity = 2.5f;
            sun.shadows = LightShadows.Soft;
            sunGo.transform.rotation = Quaternion.Euler(62f, -28f, 0f);

            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.transform.localScale = Vector3.one * 2f;
            floor.GetComponent<Renderer>().sharedMaterial = kit.MarbleFloor;

            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.transform.position = new Vector3(-3f, 1.5f, 2f);
            wall.transform.localScale = new Vector3(3f, 3f, 0.4f);
            wall.GetComponent<Renderer>().sharedMaterial = kit.Marble;

            var gold = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            gold.transform.position = new Vector3(3f, 1f, 0f);
            gold.GetComponent<Renderer>().sharedMaterial = kit.Gold;

            var water = GameObject.CreatePrimitive(PrimitiveType.Plane);
            water.transform.position = new Vector3(0f, 0.25f, 3.5f);
            water.transform.localScale = Vector3.one * 0.5f;
            water.GetComponent<Renderer>().sharedMaterial = kit.Water;

            var star = GameObject.CreatePrimitive(PrimitiveType.Cube);
            star.transform.position = new Vector3(0f, 2.4f, 0f);
            star.transform.localScale = new Vector3(1f, 1f, 0.4f);
            star.transform.rotation = Quaternion.Euler(0f, 25f, 0f);
            star.GetComponent<Renderer>().sharedMaterial = kit.SparkCore;
            var starLight = new GameObject("Spark Light").AddComponent<Light>();
            starLight.type = LightType.Point;
            starLight.transform.position = new Vector3(0f, 2.4f, 0f);
            starLight.color = new Color(1f, 0.72f, 0.35f);
            starLight.intensity = 8f;
            starLight.range = 15f;

            var halo = GameObject.CreatePrimitive(PrimitiveType.Quad);
            halo.transform.position = new Vector3(0f, 2.4f, 0.6f);
            halo.transform.localScale = Vector3.one * 5f;
            halo.GetComponent<Renderer>().sharedMaterial = kit.SparkHaloMat;

            var shaft = GameObject.CreatePrimitive(PrimitiveType.Cube);
            shaft.transform.position = new Vector3(0f, 1.2f, 2.2f);
            shaft.transform.localScale = new Vector3(1f, 4f, 1f);
            shaft.GetComponent<Renderer>().sharedMaterial = kit.LightShaftMat;
            Object.DestroyImmediate(shaft.GetComponent<BoxCollider>());

            PostFxBuilder.Build();

            var camGo = new GameObject("Cam");
            camGo.tag = "MainCamera";
            var cam = camGo.AddComponent<Camera>();
            cam.fieldOfView = 48f;
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.allowHDR = true;
            var data = camGo.AddComponent<UniversalAdditionalCameraData>();
            data.renderPostProcessing = true;
            data.antialiasing = AntialiasingMode.None;
            camGo.transform.position = new Vector3(4.5f, 3.2f, -7f);
            camGo.transform.LookAt(new Vector3(0f, 2f, 0.5f));

            var rt = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            rt.Create();
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(1280, 720, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
            tex.Apply();
            Directory.CreateDirectory("Screenshots");
            File.WriteAllBytes("Screenshots/shader_preview.png", tex.EncodeToPNG());
            RenderTexture.active = null;
            cam.targetTexture = null;
            rt.Release();
            Debug.Log("[ShaderPreviewTest] wrote Screenshots/shader_preview.png");
        }

        public static int FindQuality(string name)
        {
            for (int i = 0; i < QualitySettings.names.Length; i++)
            {
                if (QualitySettings.names[i] == name) return i;
            }
            return Mathf.Max(0, QualitySettings.names.Length - 1);
        }
    }
}
