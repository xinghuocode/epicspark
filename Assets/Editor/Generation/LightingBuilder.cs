using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace SparkCode.Generation
{
    /// <summary>Day-light, sky, interior fills, camera rig and post volume for the temple.</summary>
    public static class LightingBuilder
    {
        public struct LightingResult
        {
            public Camera Camera;
            public OrbitCamera Orbit;
            public Light Sun;
        }

        public static LightingResult Build(TempleBuildKit kit, EnvironmentInfo env)
        {
            SetupRenderSettings(kit);
            var sun = CreateSun();
            CreateOculusLight();
            CreateInteriorFills();
            PostFxBuilder.Build();
            var cam = CreateCamera(env);
            return cam;
        }

        static void SetupRenderSettings(TempleBuildKit kit)
        {
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.24f, 0.30f, 0.40f);
            RenderSettings.ambientEquatorColor = new Color(0.34f, 0.31f, 0.27f);
            RenderSettings.ambientGroundColor = new Color(0.10f, 0.09f, 0.08f);
            RenderSettings.ambientIntensity = 0.85f;

            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogDensity = 0.0045f;
            RenderSettings.fogColor = new Color(0.13f, 0.15f, 0.19f);

            var sky = kit.CreateMaterial("Sky_Temple", "SparkCode/SkyGradient");
            if (sky != null)
            {
                sky.SetColor("_ZenithColor", new Color(0.12f, 0.25f, 0.48f));
                sky.SetColor("_HorizonColor", new Color(0.74f, 0.79f, 0.83f));
                sky.SetColor("_GroundColor", new Color(0.30f, 0.28f, 0.24f));
                sky.SetColor("_SunColor", new Color(1f, 0.88f, 0.68f));
                sky.SetColor("_HazeColor", new Color(0.90f, 0.86f, 0.78f));
                sky.SetFloat("_SunSize", 1.8f);
                sky.SetFloat("_HazeStrength", 0.35f);
                sky.SetFloat("_CloudStrength", 0.14f);
                RenderSettings.skybox = sky;
            }
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Skybox;
            RenderSettings.reflectionIntensity = 0.65f;
            RenderSettings.sun = null;
        }

        static Light CreateSun()
        {
            var go = new GameObject("Sun (Directional)");
            var light = go.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = new Color(1f, 0.94f, 0.84f);
            light.intensity = 2.5f;
            light.shadows = LightShadows.Soft;
            light.shadowStrength = 0.92f;
            light.shadowBias = 0.03f;
            light.shadowNormalBias = 0.35f;
            light.shadowNearPlane = 0.2f;
            go.transform.rotation = Quaternion.Euler(70f, -25f, 0f);
            RenderSettings.sun = light;

            var sky = RenderSettings.skybox;
            if (sky != null)
            {
                Vector3 sunDir = -go.transform.forward;
                sky.SetVector("_SunDirection", new Vector4(sunDir.x, sunDir.y, sunDir.z, 0f));
            }
            return light;
        }

        static void CreateOculusLight()
        {
            var go = new GameObject("Oculus Shaft Light");
            go.transform.position = new Vector3(0f, TempleLayout.OculusY - 0.8f, 0f);
            go.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            var light = go.AddComponent<Light>();
            light.type = LightType.Spot;
            light.color = new Color(1f, 0.93f, 0.79f);
            light.intensity = 18f;
            light.range = 46f;
            light.spotAngle = 50f;
            light.innerSpotAngle = 30f;
            light.shadows = LightShadows.None;
        }

        static void CreateInteriorFills()
        {
            AddPoint("Fill Dome (cool)", new Vector3(0f, 17.5f, 0f), new Color(0.58f, 0.68f, 0.88f), 1.5f, 30f, LightShadows.None);
            AddPoint("Fill Pool (warm)", new Vector3(0f, 2.2f, 0f), new Color(1f, 0.78f, 0.48f), 1.1f, 12f, LightShadows.None);
            AddPoint("Fill Nave", new Vector3(0f, 8f, 9.5f), new Color(0.95f, 0.9f, 0.82f), 0.85f, 20f, LightShadows.None);
        }

        static void AddPoint(string name, Vector3 pos, Color color, float intensity, float range, LightShadows shadows)
        {
            var go = new GameObject(name);
            go.transform.position = pos;
            var light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = color;
            light.intensity = intensity;
            light.range = range;
            light.shadows = shadows;
        }

        static LightingResult CreateCamera(EnvironmentInfo env)
        {
            var target = new GameObject("Camera Target").transform;
            target.position = env.CameraTarget;

            var go = new GameObject("Main Camera");
            go.tag = "MainCamera";
            var cam = go.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.fieldOfView = TempleLayout.CameraFov;
            cam.nearClipPlane = 0.08f;
            cam.farClipPlane = 180f;
            cam.allowHDR = true;
            cam.allowMSAA = true;
            cam.depth = -1f;

            var data = go.AddComponent<UniversalAdditionalCameraData>();
            data.renderPostProcessing = true;
            data.antialiasing = AntialiasingMode.None;
            data.renderShadows = true;
            data.volumeLayerMask = ~0;
            data.volumeTrigger = go.transform;

            go.AddComponent<AudioListener>();

            var orbit = go.AddComponent<OrbitCamera>();
            orbit.target = target;
            orbit.radius = TempleLayout.CameraOrbitRadius;
            orbit.height = TempleLayout.CameraHeight;
            orbit.lookHeight = TempleLayout.CameraLookHeight;
            orbit.fieldOfView = TempleLayout.CameraFov;
            orbit.introRadius = 20.5f;
            orbit.introDuration = 8f;
            orbit.swayDegrees = 2.4f;
            orbit.swaySpeed = 0.022f;
            orbit.bobAmplitude = 0.06f;
            orbit.bobSpeed = 0.08f;
            orbit.orbitSpeed = 0f;
            orbit.focusRadius = 7.2f;
            orbit.focusLookHeight = 7.2f;
            orbit.focusBlendSpeed = 0.5f;

            // sensible opening frame for edit-mode previews / screenshots
            float a = 14f * Mathf.Deg2Rad;
            Vector3 pos = target.position + new Vector3(Mathf.Sin(a) * TempleLayout.CameraOrbitRadius, TempleLayout.CameraHeight, Mathf.Cos(a) * TempleLayout.CameraOrbitRadius);
            go.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(target.position + Vector3.up * TempleLayout.CameraLookHeight - pos, Vector3.up));

            return new LightingResult { Camera = cam, Orbit = orbit, Sun = RenderSettings.sun };
        }
    }
}
