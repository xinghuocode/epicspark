using System.IO;
using SparkCode;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace SparkCode.Generation
{
    /// <summary>
    /// Offline screenshot tool: opens the generated StartScene, poses the camera for several framings,
    /// forces the menu into main/settings/about/hover states and renders PNGs into Screenshots/.
    /// Run via: Unity.exe -batchmode -executeMethod SparkCode.Generation.SceneCapture.CaptureAll -quit
    /// </summary>
    public static class SceneCapture
    {
        struct Shot
        {
            public string FileName;
            public Vector3 Position;
            public Vector3 LookAt;
            public float FieldOfView;
            public string MenuState;
            public int HoverIndex;
            public bool HideMenu;
            public int Width;
            public int Height;
        }

        public static void CaptureAll()
        {
            EditorSceneManager.OpenScene(TempleLayout.StartScenePath);
            var cam = Camera.main;
            if (cam == null)
            {
                foreach (var c in Object.FindObjectsOfType<Camera>())
                {
                    if (c.CompareTag("MainCamera")) { cam = c; break; }
                }
            }
            if (cam == null)
            {
                Debug.LogError("[SceneCapture] No main camera found; open StartScene first.");
                return;
            }

            var orbit = cam.GetComponent<OrbitCamera>();
            if (orbit != null) orbit.enabled = false;

            var menu = Object.FindObjectOfType<StartMenuController>();
            var canvas = menu != null ? menu.GetComponentInParent<Canvas>() : null;
            if (canvas == null) canvas = Object.FindObjectOfType<Canvas>();

            var dof = FindDepthOfField();

            Directory.CreateDirectory("Screenshots");

            var shots = new[]
            {
                new Shot { FileName = "01_start_menu.png", Position = new Vector3(3.6f, 3.3f, 14.2f), LookAt = new Vector3(0f, 5.6f, 0f), FieldOfView = 46f, MenuState = "main", HoverIndex = -1, Width = 1920, Height = 1080 },
                new Shot { FileName = "02_star_close.png", Position = new Vector3(1.4f, 6.0f, 9.6f), LookAt = new Vector3(0f, 7.3f, 0f), FieldOfView = 42f, MenuState = "main", HoverIndex = -1, HideMenu = true, Width = 1920, Height = 1080 },
                new Shot { FileName = "03_dome_oculus.png", Position = new Vector3(1.0f, 2.2f, 11.0f), LookAt = new Vector3(0f, 17.0f, 0f), FieldOfView = 63f, MenuState = "main", HoverIndex = -1, HideMenu = true, Width = 1920, Height = 1080 },
                new Shot { FileName = "04_pool_island.png", Position = new Vector3(6.2f, 1.8f, 8.4f), LookAt = new Vector3(0f, 1.2f, 0f), FieldOfView = 48f, MenuState = "main", HoverIndex = -1, HideMenu = true, Width = 1920, Height = 1080 },
                new Shot { FileName = "05_colonnade.png", Position = new Vector3(11.5f, 3.2f, 11.5f), LookAt = new Vector3(0f, 8.0f, 0f), FieldOfView = 58f, MenuState = "main", HoverIndex = -1, HideMenu = true, Width = 1920, Height = 1080 },
                new Shot { FileName = "06_menu_hover.png", Position = new Vector3(3.6f, 3.3f, 14.2f), LookAt = new Vector3(0f, 5.6f, 0f), FieldOfView = 46f, MenuState = "main", HoverIndex = 0, Width = 1920, Height = 1080 },
                new Shot { FileName = "07_settings.png", Position = new Vector3(3.6f, 3.3f, 14.2f), LookAt = new Vector3(0f, 5.6f, 0f), FieldOfView = 46f, MenuState = "settings", HoverIndex = -1, Width = 1920, Height = 1080 },
                new Shot { FileName = "08_about.png", Position = new Vector3(3.6f, 3.3f, 14.2f), LookAt = new Vector3(0f, 5.6f, 0f), FieldOfView = 46f, MenuState = "about", HoverIndex = -1, Width = 1920, Height = 1080 },
                new Shot { FileName = "09_started.png", Position = new Vector3(1.8f, 4.4f, 11.4f), LookAt = new Vector3(0f, 7.2f, 0f), FieldOfView = 46f, MenuState = "started", HoverIndex = -1, Width = 1920, Height = 1080 },
                new Shot { FileName = "10_final_wide_2560x1600.png", Position = new Vector3(3.4f, 3.4f, 14.4f), LookAt = new Vector3(0f, 5.4f, 0f), FieldOfView = 46f, MenuState = "main", HoverIndex = -1, Width = 2560, Height = 1600 },
            };

            foreach (var shot in shots)
            {
                CaptureShot(cam, menu, canvas, dof, shot);
                Debug.Log("[SceneCapture] captured " + shot.FileName);
            }

            if (menu != null)
            {
                menu.PrepareCapture("main", -1);
                if (canvas != null) canvas.gameObject.SetActive(true);
            }
            if (orbit != null) orbit.enabled = true;
            if (dof != null) dof.focusDistance.value = 13.5f;

            AssetDatabase.Refresh();
            Debug.Log("[SceneCapture] all screenshots written to Screenshots/");
        }

        static void CaptureShot(Camera cam, StartMenuController menu, Canvas canvas, DepthOfField dof, Shot shot)
        {
            if (menu != null)
            {
                menu.PrepareCapture(shot.MenuState, shot.HoverIndex);
            }

            if (canvas != null)
            {
                canvas.gameObject.SetActive(!shot.HideMenu && !string.IsNullOrEmpty(shot.MenuState));
            }

            cam.transform.SetPositionAndRotation(shot.Position, Quaternion.LookRotation((shot.LookAt - shot.Position).normalized, Vector3.up));
            cam.fieldOfView = shot.FieldOfView;

            if (dof != null)
            {
                dof.focusDistance.value = Mathf.Max(2f, Vector3.Distance(shot.Position, shot.LookAt));
                dof.focusDistance.Override(dof.focusDistance.value);
            }

            SimulateParticles();
            Canvas.ForceUpdateCanvases();

            var rt = new RenderTexture(shot.Width, shot.Height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
            {
                name = "Capture_" + shot.FileName
            };
            rt.Create();
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(shot.Width, shot.Height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, shot.Width, shot.Height), 0, 0);
            tex.Apply();
            File.WriteAllBytes(Path.Combine("Screenshots", shot.FileName), tex.EncodeToPNG());
            RenderTexture.active = null;
            cam.targetTexture = null;
            rt.Release();
            Object.DestroyImmediate(rt);
            Object.DestroyImmediate(tex);
        }

        static void SimulateParticles()
        {
            foreach (var ps in Object.FindObjectsOfType<ParticleSystem>(true))
            {
                if (ps == null) continue;
                ps.Simulate(4.5f, true, true);
            }
        }

        static DepthOfField FindDepthOfField()
        {
            foreach (var volume in Object.FindObjectsOfType<Volume>())
            {
                if (volume.profile == null) continue;
                if (volume.profile.TryGet(out DepthOfField dof)) return dof;
            }
            return null;
        }
    }
}
