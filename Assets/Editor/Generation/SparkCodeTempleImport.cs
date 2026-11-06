using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SparkCode;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace SparkCode.Generation
{
    /// <summary>
    /// Imports the final Blender temple (Assets/Art/Models/SparkCodeTemple.fbx) into a standalone,
    /// presentation-ready URP scene. The existing StartScene is left untouched.
    ///
    /// Batch entry points:
    ///   Unity.exe -batchmode -projectPath . -executeMethod SparkCode.Generation.SparkCodeTempleImport.BuildAndCapture -quit
    ///   Unity.exe -batchmode -projectPath . -executeMethod SparkCode.Generation.SparkCodeTempleImport.Capture -quit
    /// </summary>
    public static class SparkCodeTempleImport
    {
        public const string ModelPath = "Assets/Art/Models/SparkCodeTemple.fbx";
        public const string ScenePath = "Assets/Scenes/SparkCodeTemple.unity";
        public const string GeneratedFolder = "Assets/Generated/SparkCodeTemple";
        public const string ProfilePath = "Assets/Generated/Profiles/SparkCodeTemplePost.asset";

        const string MatMarbleWhite = "Assets/Generated/Materials/Marble_White.mat";
        const string MatMarbleTrim = "Assets/Generated/Materials/Marble_Trim.mat";
        const string MatMarbleDark = "Assets/Generated/Materials/Marble_Dark.mat";
        const string MatMarbleFloor = "Assets/Generated/Materials/Marble_Floor.mat";
        const string MatMarbleBasin = "Assets/Generated/Materials/Marble_Basin.mat";
        const string MatGold = "Assets/Generated/Materials/Gold_Polished.mat";
        const string MatWater = "Assets/Generated/Materials/Water_Ring.mat";
        const string MatSparkCore = "Assets/Generated/Materials/Spark_Core.mat";
        const string MatLightShaft = "Assets/Generated/Materials/Temple_LightShaft.mat";
        const string MatOculusGlow = "Assets/Generated/Materials/Temple_OculusGlow.mat";

        static readonly Vector3 StarPosition = new Vector3(0f, 5.05f, 0f);
        static readonly Vector3 OculusPosition = new Vector3(0f, 18.25f, 0f);

        [MenuItem("SparkCode/导入 Blender 星火神殿场景", false, 30)]
        public static void BuildAndCapture()
        {
            Build();
            Capture();
        }

        [MenuItem("SparkCode/重新导入 Blender 神殿模型", false, 31)]
        public static void Build()
        {
            try
            {
                EnsureFolder(GeneratedFolder);
                EnsureFolder("Assets/Generated/Meshes");
                EnsureFolder("Assets/Generated/Profiles");

                int quality = ShaderPreviewTest.FindQuality("High Fidelity");
                if (quality >= 0) QualitySettings.SetQualityLevel(quality, true);

                EnsureModelImportSettings();
                AssetDatabase.ImportAsset(ModelPath, ImportAssetOptions.ForceSynchronousImport);
                var asset = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
                if (asset == null)
                    throw new InvalidOperationException("FBX not found/imported: " + ModelPath);

                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

                var root = new GameObject("SparkCodeTemple");
                var instance = PrefabUtility.InstantiatePrefab(asset) as GameObject;
                if (instance == null) instance = UnityEngine.Object.Instantiate(asset);
                instance.name = "Temple_Blender";
                instance.transform.SetParent(root.transform, false);
                instance.transform.localPosition = Vector3.zero;
                // Keep the importer's axis-conversion rotation. Blender Z-up geometry must stay upright.
                // Unpack so the star can be re-parented to an animated pivot.
                if (PrefabUtility.IsPartOfPrefabInstance(instance))
                    PrefabUtility.UnpackPrefabInstance(instance, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);

                Bounds bounds = CalculateBounds(instance);
                Debug.Log("[SparkCodeTempleImport] imported bounds size=" + bounds.size + " center=" + bounds.center);

                // FBX unit conversion sanity check: the authored temple is about 19 m tall.
                if (bounds.size.y > 100f || (bounds.size.y > 0.1f && bounds.size.y < 5f))
                {
                    float s = 19.0f / Mathf.Max(0.0001f, bounds.size.y);
                    instance.transform.localScale *= s;
                    bounds = CalculateBounds(instance);
                    Debug.Log("[SparkCodeTempleImport] corrected model scale by " + s + " -> size=" + bounds.size);
                }

                AssignMaterials(instance);
                ConfigureSkyAndRenderSettings();

                var lights = BuildLighting(root.transform);
                var starPivot = GroupStar(instance.transform, root.transform);
                BuildStarAura(starPivot);
                BuildShafts(root.transform);

                var cameras = BuildCameras(root.transform);
                var controller = root.AddComponent<TempleShowcaseController>();
                controller.cameras = cameras;
                controller.starPivot = starPivot;

                BuildPostVolume();
                BuildReflectionProbe(root.transform);

                // Keep only the hero camera enabled when the scene is opened.
                for (int i = 0; i < cameras.Length; i++) cameras[i].enabled = i == 0;

                EditorSceneManager.MarkSceneDirty(scene);
                bool saved = EditorSceneManager.SaveScene(scene, ScenePath);
                if (!saved) throw new InvalidOperationException("Failed to save " + ScenePath);

                AddSceneToBuildSettings(ScenePath);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                LogStats();
                Debug.Log("[SparkCodeTempleImport] scene saved: " + ScenePath + " lights=" + lights);
            }
            catch (Exception e)
            {
                Debug.LogError("[SparkCodeTempleImport] BUILD FAILED: " + e);
                throw;
            }
        }

        // ------------------------------------------------------------------ import / materials

        static void EnsureModelImportSettings()
        {
            var importer = AssetImporter.GetAtPath(ModelPath) as ModelImporter;
            if (importer == null) throw new InvalidOperationException("No ModelImporter for " + ModelPath);

            importer.globalScale = 1f;
            importer.useFileUnits = true;
            importer.importCameras = false;
            importer.importLights = false;
            importer.importAnimation = false;
            importer.animationType = ModelImporterAnimationType.None;
            importer.importBlendShapes = false;
            importer.importVisibility = false;
            importer.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
            importer.importNormals = ModelImporterNormals.Import;
            importer.importTangents = ModelImporterTangents.CalculateMikk;
            importer.meshCompression = ModelImporterMeshCompression.Off;
            importer.isReadable = true;
            importer.generateSecondaryUV = false;
            importer.weldVertices = true;
            importer.SaveAndReimport();
        }

        static Material Load(string path)
        {
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null) Debug.LogWarning("[SparkCodeTempleImport] missing material: " + path);
            return m;
        }

        static Material CloneMaterial(Material source, string name)
        {
            if (source == null) return null;
            string path = GeneratedFolder + "/" + name + ".mat";
            var old = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (old != null) AssetDatabase.DeleteAsset(path);
            var clone = new Material(source) { name = name };
            AssetDatabase.CreateAsset(clone, path);
            return clone;
        }

        static void AssignMaterials(GameObject instance)
        {
            var white = Load(MatMarbleWhite);
            var trim = Load(MatMarbleTrim);
            var dark = Load(MatMarbleDark);
            var floor = Load(MatMarbleFloor);
            var basin = Load(MatMarbleBasin);
            var gold = Load(MatGold);
            var waterBase = Load(MatWater);
            var sparkBase = Load(MatSparkCore);

            var water = CloneMaterial(waterBase, "Water_TempleScene");
            if (water != null)
            {
                if (water.HasProperty("_ShallowColor")) water.SetColor("_ShallowColor", new Color(0.035f, 0.135f, 0.155f));
                if (water.HasProperty("_DeepColor")) water.SetColor("_DeepColor", new Color(0.006f, 0.03f, 0.042f));
                if (water.HasProperty("_FresnelColor")) water.SetColor("_FresnelColor", new Color(0.18f, 0.32f, 0.38f));
                if (water.HasProperty("_FoamColor")) water.SetColor("_FoamColor", new Color(0.05f, 0.17f, 0.18f));
                if (water.HasProperty("_NormalStrength")) water.SetFloat("_NormalStrength", 0.30f);
                if (water.HasProperty("_Glitter")) water.SetFloat("_Glitter", 0.04f);
                if (water.HasProperty("_Alpha")) water.SetFloat("_Alpha", 0.93f);
                EditorUtility.SetDirty(water);
            }

            var spark = CloneMaterial(sparkBase, "Spark_StarScene");
            if (spark != null)
            {
                if (spark.HasProperty("_Color")) spark.SetColor("_Color", new Color(1f, 0.66f, 0.24f));
                if (spark.HasProperty("_EmissionColor")) spark.SetColor("_EmissionColor", new Color(1f, 0.52f, 0.13f));
                if (spark.HasProperty("_EmissionIntensity")) spark.SetFloat("_EmissionIntensity", 2.6f);
                if (spark.HasProperty("_FresnelColor")) spark.SetColor("_FresnelColor", new Color(1f, 0.88f, 0.55f));
                if (spark.HasProperty("_SparkleIntensity")) spark.SetFloat("_SparkleIntensity", 2.4f);
                if (spark.HasProperty("_SparkleScale")) spark.SetFloat("_SparkleScale", 18f);
                EditorUtility.SetDirty(spark);
            }
            AssetDatabase.SaveAssets();

            var renderers = instance.GetComponentsInChildren<MeshRenderer>(true);
            foreach (var r in renderers)
            {
                string obj = r.gameObject.name.ToLowerInvariant();
                bool isStarObject = obj.Contains("star") || obj.Contains("spark") ||
                                    obj.Contains("dust") || obj.Contains("ribbon") || obj.Contains("mini");
                var mats = r.sharedMaterials;
                if (mats == null || mats.Length == 0) mats = new Material[1];

                for (int i = 0; i < mats.Length; i++)
                {
                    string key = mats[i] != null ? mats[i].name.ToLowerInvariant() : string.Empty;
                    Material target = white;

                    if (isStarObject) target = spark;
                    else if (key.Contains("water") || obj.Contains("water")) target = water;
                    else if (key.Contains("pool") || obj.Contains("pool")) target = basin;
                    else if (key.Contains("floor") || obj.Contains("floor")) target = floor;
                    else if (key.Contains("gold")) target = gold;
                    else if (key.Contains("dark")) target = dark;
                    else if (key.Contains("light") || key.Contains("trim")) target = trim;
                    else if (key.Contains("star") || key.Contains("spark") || key.Contains("dust") ||
                             key.Contains("ribbon") || key.Contains("mini"))
                        target = spark;
                    else if (obj.Contains("dome")) target = white;
                    else if (obj.Contains("column")) target = white;

                    mats[i] = target;
                }

                r.sharedMaterials = mats;

                bool transparentOrGlow = obj.Contains("water") || obj.Contains("star") ||
                                         obj.Contains("spark") || obj.Contains("dust") ||
                                         obj.Contains("ribbon") || obj.Contains("mini") ||
                                         r.sharedMaterial == water || r.sharedMaterial == spark;
                r.shadowCastingMode = transparentOrGlow ? ShadowCastingMode.Off : ShadowCastingMode.On;
                r.receiveShadows = !transparentOrGlow;
                r.lightProbeUsage = transparentOrGlow ? LightProbeUsage.Off : LightProbeUsage.BlendProbes;
                r.reflectionProbeUsage = transparentOrGlow ? ReflectionProbeUsage.Off : ReflectionProbeUsage.BlendProbes;
            }

            // Colliders need a readable floor; the materials above are all shared assets.
            foreach (var r in renderers)
            {
                if (!r.gameObject.name.ToLowerInvariant().Contains("floor") &&
                    !r.gameObject.name.ToLowerInvariant().Contains("island")) continue;
                var mf = r.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                var mc = r.gameObject.GetComponent<MeshCollider>();
                if (mc == null) mc = r.gameObject.AddComponent<MeshCollider>();
                mc.sharedMesh = mf.sharedMesh;
                mc.convex = false;
            }
        }

        // ------------------------------------------------------------------ environment

        static void ConfigureSkyAndRenderSettings()
        {
            var sky = Load("Assets/Generated/Materials/Sky_Temple.mat");
            if (sky != null)
            {
                if (sky.HasProperty("_ZenithColor")) sky.SetColor("_ZenithColor", new Color(0.10f, 0.22f, 0.44f));
                if (sky.HasProperty("_HorizonColor")) sky.SetColor("_HorizonColor", new Color(0.72f, 0.78f, 0.84f));
                if (sky.HasProperty("_GroundColor")) sky.SetColor("_GroundColor", new Color(0.24f, 0.23f, 0.21f));
                if (sky.HasProperty("_SunColor")) sky.SetColor("_SunColor", new Color(1f, 0.87f, 0.66f));
                if (sky.HasProperty("_HazeColor")) sky.SetColor("_HazeColor", new Color(0.89f, 0.84f, 0.75f));
                if (sky.HasProperty("_SunSize")) sky.SetFloat("_SunSize", 1.8f);
                if (sky.HasProperty("_HazeStrength")) sky.SetFloat("_HazeStrength", 0.35f);
                if (sky.HasProperty("_CloudStrength")) sky.SetFloat("_CloudStrength", 0.16f);
                RenderSettings.skybox = sky;
            }

            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.20f, 0.27f, 0.38f);
            RenderSettings.ambientEquatorColor = new Color(0.34f, 0.31f, 0.27f);
            RenderSettings.ambientGroundColor = new Color(0.10f, 0.09f, 0.08f);
            RenderSettings.ambientIntensity = 0.85f;

            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogDensity = 0.0065f;
            RenderSettings.fogColor = new Color(0.12f, 0.145f, 0.18f);
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Skybox;
            RenderSettings.reflectionIntensity = 0.72f;
        }

        // ------------------------------------------------------------------ lighting

        static int BuildLighting(Transform parent)
        {
            int count = 0;

            var sunGo = new GameObject("Sun (Oculus)");
            sunGo.transform.SetParent(parent, false);
            sunGo.transform.rotation = Quaternion.LookRotation(new Vector3(0.16f, -1f, 0.07f).normalized);
            var sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.color = new Color(1f, 0.93f, 0.82f);
            sun.intensity = 4.0f;
            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = 0.94f;
            sun.shadowBias = 0.03f;
            sun.shadowNormalBias = 0.35f;
            RenderSettings.sun = sun;
            count++;

            // Soft frontal fill so the camera-side colonnade is not crushed to black.
            var fillSunGo = new GameObject("Cool Frontal Fill");
            fillSunGo.transform.SetParent(parent, false);
            fillSunGo.transform.rotation = Quaternion.LookRotation(new Vector3(-0.38f, -0.52f, -0.76f).normalized);
            var fillSun = fillSunGo.AddComponent<Light>();
            fillSun.type = LightType.Directional;
            fillSun.color = new Color(0.62f, 0.72f, 0.88f);
            fillSun.intensity = 0.45f;
            fillSun.shadows = LightShadows.None;
            count++;

            var sky = RenderSettings.skybox;
            if (sky != null && sky.HasProperty("_SunDirection"))
                sky.SetVector("_SunDirection", new Vector4(-sunGo.transform.forward.x, -sunGo.transform.forward.y,
                                                           -sunGo.transform.forward.z, 0f));

            // Warm shaft light falling through the oculus.
            var shaftGo = new GameObject("Oculus Shaft Light");
            shaftGo.transform.SetParent(parent, false);
            shaftGo.transform.position = OculusPosition + Vector3.down * 0.6f;
            shaftGo.transform.rotation = Quaternion.LookRotation(Vector3.down);
            var shaft = shaftGo.AddComponent<Light>();
            shaft.type = LightType.Spot;
            shaft.color = new Color(1f, 0.91f, 0.74f);
            shaft.intensity = 16f;
            shaft.range = 34f;
            shaft.spotAngle = 56f;
            shaft.innerSpotAngle = 28f;
            shaft.shadows = LightShadows.None;
            count++;

            AddPoint(parent, "Fill Dome (cool)", new Vector3(0f, 11.5f, 0f),
                new Color(0.45f, 0.58f, 0.82f), 3.2f, 26f, LightShadows.None);
            AddPoint(parent, "Fill Nave (warm)", new Vector3(0f, 4.2f, 6.0f),
                new Color(1f, 0.82f, 0.62f), 2.8f, 18f, LightShadows.None);
            AddPoint(parent, "Fill Pool (teal)", new Vector3(0f, 0.8f, 0f),
                new Color(0.25f, 0.68f, 0.62f), 1.4f, 12f, LightShadows.None);
            AddPoint(parent, "South Colonnade Fill", new Vector3(0f, 3.2f, 9.8f),
                new Color(0.55f, 0.70f, 0.95f), 2.4f, 14f, LightShadows.None);
            AddPoint(parent, "North Colonnade Fill", new Vector3(0f, 3.2f, -9.8f),
                new Color(1f, 0.78f, 0.56f), 1.6f, 13f, LightShadows.None);

            // Cool wall grazes.
            for (int i = 0; i < 4; i++)
            {
                float a = (-90f + 35f + i * 90f) * Mathf.Deg2Rad;
                var p = new Vector3(8.3f * Mathf.Cos(a), 2.2f, -8.3f * Mathf.Sin(a));
                AddPoint(parent, "Wall Fill " + i, p, new Color(0.55f, 0.68f, 0.90f), 1.1f, 9f, LightShadows.None);
            }

            // Dome uplights.
            for (int i = 0; i < 4; i++)
            {
                float a = (-90f + 22.5f + i * 90f) * Mathf.Deg2Rad;
                var p = new Vector3(8.6f * Mathf.Cos(a), 7.25f, -8.6f * Mathf.Sin(a));
                AddSpot(parent, "Dome Uplight " + i, p, new Vector3(0f, 15.5f, 0f),
                    new Color(1f, 0.80f, 0.56f), 3.0f, 20f, 60f, LightShadows.None);
            }

            // Warm colonnade feet and teal pool underglow.
            for (int i = 0; i < 6; i++)
            {
                float a = (-90f + 30f + i * 60f) * Mathf.Deg2Rad;
                var p = new Vector3(9f * Mathf.Cos(a), 1.5f, -9f * Mathf.Sin(a));
                AddPoint(parent, "Floor Graze " + i, p, new Color(1f, 0.72f, 0.46f), 0.9f, 6f, LightShadows.None);
            }
            for (int i = 0; i < 3; i++)
            {
                float a = (-90f + 50f + i * 120f) * Mathf.Deg2Rad;
                var p = new Vector3(4.9f * Mathf.Cos(a), -0.38f, -4.9f * Mathf.Sin(a));
                AddPoint(parent, "Pool Under " + i, p, new Color(0.16f, 0.65f, 0.62f), 0.7f, 4.5f, LightShadows.None);
            }

            // Star illumination: a key and two warm accents.
            var key = AddPoint(parent, "Spark Key", StarPosition, new Color(1f, 0.53f, 0.16f),
                6.5f, 18f, LightShadows.Soft);
            key.shadowStrength = 0.55f;
            AddPoint(parent, "Spark Rim", StarPosition + new Vector3(0.9f, 0.65f, 0.7f),
                new Color(1f, 0.27f, 0.05f), 1.8f, 8f, LightShadows.None);
            AddPoint(parent, "Spark Fill", StarPosition + new Vector3(-1.1f, -0.6f, -0.7f),
                new Color(1f, 0.70f, 0.32f), 1.2f, 8f, LightShadows.None);

            return count;
        }

        static Light AddPoint(Transform parent, string name, Vector3 position, Color color,
                              float intensity, float range, LightShadows shadows)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.position = position;
            var l = go.AddComponent<Light>();
            l.type = LightType.Point;
            l.color = color;
            l.intensity = intensity;
            l.range = range;
            l.shadows = shadows;
            return l;
        }

        static Light AddSpot(Transform parent, string name, Vector3 position, Vector3 target,
                             Color color, float intensity, float range, float angle, LightShadows shadows)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.position = position;
            go.transform.rotation = Quaternion.LookRotation((target - position).normalized, Vector3.up);
            var l = go.AddComponent<Light>();
            l.type = LightType.Spot;
            l.color = color;
            l.intensity = intensity;
            l.range = range;
            l.spotAngle = angle;
            l.innerSpotAngle = angle * 0.55f;
            l.shadows = shadows;
            return l;
        }

        // ------------------------------------------------------------------ star grouping

        static Transform GroupStar(Transform model, Transform root)
        {
            var pivotGo = new GameObject("Spark Pivot");
            pivotGo.transform.SetParent(root, false);
            pivotGo.transform.position = StarPosition;

            var starObjects = new List<Transform>();
            foreach (var t in model.GetComponentsInChildren<Transform>(true))
            {
                if (t == model) continue;
                string n = t.name.ToLowerInvariant();
                if (n.StartsWith("spark") || n.StartsWith("star") || n.Contains("ribbon") || n.Contains("dust"))
                    starObjects.Add(t);
            }

            // Only reparent top-level star objects; child meshes come along automatically.
            foreach (var t in starObjects)
            {
                if (t == null) continue;
                if (t.parent != null && starObjects.Contains(t.parent)) continue;
                t.SetParent(pivotGo.transform, true);
            }

            Debug.Log("[SparkCodeTempleImport] star objects grouped: " + starObjects.Count);
            return pivotGo.transform;
        }

        // ------------------------------------------------------------------ star aura

        static void BuildStarAura(Transform starPivot)
        {
            var quad = AssetDatabase.LoadAssetAtPath<Mesh>("Assets/Generated/Meshes/Spark_HaloQuad.asset");
            if (quad == null) quad = AssetDatabase.LoadAssetAtPath<Mesh>("Assets/Generated/Meshes/Spark_GlowQuad.asset");
            if (quad == null) return;

            var haloBase = Load("Assets/Generated/Materials/Spark_Halo.mat");
            var innerBase = Load("Assets/Generated/Materials/Spark_Halo_Inner.mat");
            var haloMat = CloneMaterial(haloBase, "Spark_HaloScene");
            if (haloMat != null)
            {
                if (haloMat.HasProperty("_Intensity")) haloMat.SetFloat("_Intensity", 1.55f);
                if (haloMat.HasProperty("_Color")) haloMat.SetColor("_Color", new Color(1f, 0.66f, 0.24f, 1f));
                if (haloMat.HasProperty("_SoftFade")) haloMat.SetFloat("_SoftFade", 0.82f);
                EditorUtility.SetDirty(haloMat);
            }
            var innerMat = CloneMaterial(innerBase, "Spark_HaloScene_Inner");
            if (innerMat != null)
            {
                if (innerMat.HasProperty("_Intensity")) innerMat.SetFloat("_Intensity", 1.5f);
                if (innerMat.HasProperty("_Color")) innerMat.SetColor("_Color", new Color(1f, 0.82f, 0.46f, 1f));
                if (innerMat.HasProperty("_SoftFade")) innerMat.SetFloat("_SoftFade", 0.55f);
                EditorUtility.SetDirty(innerMat);
            }
            AssetDatabase.SaveAssets();

            CreateAuraQuad("HaloOuter", quad, haloMat, starPivot, 6.9f, 0f, 2.1f);
            CreateAuraQuad("HaloInner", quad, innerMat, starPivot, 4.3f, 18f, -3.4f);
            var rays = CreateAuraQuad("HaloRays", quad, haloMat, starPivot, 8.8f, 45f, 0.8f);
            rays.GetComponent<MeshRenderer>().sharedMaterial = innerMat; // softer, broad rays

            // Floating golden motes around the star.
            var moteGo = new GameObject("Spark Motes");
            moteGo.transform.SetParent(starPivot, false);
            var ps = moteGo.AddComponent<ParticleSystem>();
            var main = ps.main;
            main.duration = 12f;
            main.loop = true;
            main.prewarm = true;
            main.playOnAwake = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(4.5f, 9.5f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.02f, 0.18f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.035f, 0.14f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(1f, 0.94f, 0.70f, 1f), new Color(1f, 0.60f, 0.18f, 1f));
            main.gravityModifier = 0f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            main.maxParticles = 260;

            var emission = ps.emission;
            emission.enabled = true;
            emission.rateOverTime = 26f;

            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 2.35f;
            shape.radiusThickness = 0.25f;

            var col = ps.colorOverLifetime;
            col.enabled = true;
            var grad = new Gradient();
            grad.SetKeys(
                new[] { new GradientColorKey(new Color(1f, 0.78f, 0.35f), 0f), new GradientColorKey(new Color(1f, 0.55f, 0.12f), 0.55f), new GradientColorKey(new Color(1f, 0.92f, 0.70f), 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(0.85f, 0.18f), new GradientAlphaKey(0.65f, 0.7f), new GradientAlphaKey(0f, 1f) });
            col.color = new ParticleSystem.MinMaxGradient(grad);

            var size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 0.4f, 1f, 1f));

            var pr = moteGo.GetComponent<ParticleSystemRenderer>();
            pr.sharedMaterial = Load("Assets/Generated/Materials/Spark_Mote.mat");
            pr.renderMode = ParticleSystemRenderMode.Billboard;
            pr.alignment = ParticleSystemRenderSpace.View;
            pr.shadowCastingMode = ShadowCastingMode.Off;
            pr.receiveShadows = false;
            pr.reflectionProbeUsage = ReflectionProbeUsage.Off;
        }

        static GameObject CreateAuraQuad(string name, Mesh quad, Material mat, Transform parent,
                                         float scale, float roll, float rollSpeed)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localScale = Vector3.one * scale;
            go.transform.localRotation = Quaternion.Euler(0f, 0f, roll);
            go.AddComponent<MeshFilter>().sharedMesh = quad;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = LightProbeUsage.Off;
            mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
            var billboard = go.AddComponent<Billboard>();
            billboard.rollSpeed = rollSpeed;
            billboard.baseRoll = roll;
            return go;
        }

        // ------------------------------------------------------------------ additive atmosphere

        static void BuildShafts(Transform root)
        {
            var shaftMatBase = Load(MatLightShaft);
            var shaftMat = CloneMaterial(shaftMatBase, "LightShaft_TempleScene");
            if (shaftMat != null)
            {
                if (shaftMat.HasProperty("_Intensity")) shaftMat.SetFloat("_Intensity", 1.05f);
                if (shaftMat.HasProperty("_Color")) shaftMat.SetColor("_Color", new Color(1f, 0.91f, 0.72f, 1f));
                EditorUtility.SetDirty(shaftMat);
            }
            var shaftTop = new Vector3(0f, 18.15f, 0f);
            var shaftBottom = new Vector3(0f, 1.0f, 0.15f);
            Mesh shaftMesh = SparkStarMesh.CreateShaft(
                "SparkCodeTemple_Shaft", shaftTop, shaftBottom,
                2.0f, 3.7f, 56, 8, 0.22f, 0.05f,
                new[] { 0.55f, 0.85f, 1.18f }, new[] { 0.55f, 0.30f, 0.12f });
            SaveMesh(shaftMesh, GeneratedFolder + "/SparkCodeTemple_Shaft.asset");
            CreateAdditiveObject("Light Shaft", shaftMesh, shaftMat, root, Vector3.one);

            Mesh thinMesh = SparkStarMesh.CreateShaft(
                "SparkCodeTemple_Shaft_Thin", shaftTop + new Vector3(0.15f, -0.5f, 0.25f),
                new Vector3(-0.35f, 1.3f, 1.1f),
                0.85f, 1.7f, 40, 6, 0.16f, 0.03f,
                new[] { 0.7f, 1.0f }, new[] { 0.5f, 0.2f });
            SaveMesh(thinMesh, GeneratedFolder + "/SparkCodeTemple_Shaft_Thin.asset");
            CreateAdditiveObject("Light Shaft Thin", thinMesh, shaftMat, root, Vector3.one);

            // Bright oculus disc and a warm light footprint on the island.
            var quad = AssetDatabase.LoadAssetAtPath<Mesh>("Assets/Generated/Meshes/Spark_GlowQuad.asset");
            if (quad != null)
            {
                var glowMat = Load(MatOculusGlow);
                var glow = CreateAdditiveObject("Oculus Glow", quad, glowMat, root, Vector3.one * 4.4f);
                glow.transform.position = OculusPosition + Vector3.down * 0.25f;
                glow.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            }
        }

        static GameObject CreateAdditiveObject(string name, Mesh mesh, Material mat, Transform parent, Vector3 scale)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localScale = scale;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = LightProbeUsage.Off;
            mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
            return go;
        }

        static void SaveMesh(Mesh mesh, string path)
        {
            var old = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (old != null) AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(mesh, path);
        }

        // ------------------------------------------------------------------ cameras

        static Camera[] BuildCameras(Transform parent)
        {
            var hero = MakeCamera("CAM_Hero", parent, new Vector3(0f, 1.62f, 11.90f), new Vector3(0f, 4.70f, 0f), 64.7f, true);
            var oculus = MakeCamera("CAM_Oculus", parent, new Vector3(1.2f, 1.10f, 8.30f), new Vector3(0f, 8.35f, 0f), 71.0f, false);
            var star = MakeCamera("CAM_Star", parent, new Vector3(1.35f, 2.95f, 8.30f), new Vector3(0f, 5.05f, 0f), 30.0f, false);
            var wide = MakeCamera("CAM_Wide", parent, new Vector3(-4.90f, 1.55f, 10.52f), new Vector3(0f, 4.90f, 0f), 45.8f, false);
            return new[] { hero, oculus, star, wide };
        }

        static Camera MakeCamera(string name, Transform parent, Vector3 position, Vector3 target, float fov, bool main)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.position = position;
            go.transform.rotation = Quaternion.LookRotation((target - position).normalized, Vector3.up);
            var cam = go.AddComponent<Camera>();
            cam.fieldOfView = fov;
            cam.nearClipPlane = 0.06f;
            cam.farClipPlane = 260f;
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.allowHDR = true;
            cam.allowMSAA = true;
            cam.depth = main ? 0f : -1f;
            if (main) go.tag = "MainCamera";

            var data = go.AddComponent<UniversalAdditionalCameraData>();
            data.renderPostProcessing = true;
            data.antialiasing = AntialiasingMode.FastApproximateAntialiasing;
            data.renderShadows = true;
            data.volumeLayerMask = ~0;
            data.volumeTrigger = go.transform;

            var listener = go.AddComponent<AudioListener>();
            listener.enabled = main;
            cam.enabled = main;
            return cam;
        }

        // ------------------------------------------------------------------ post / probe

        static void BuildPostVolume()
        {
            var profile = CreateProfile();
            var go = new GameObject("Global Volume (Temple Post)");
            var volume = go.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = 10f;
            volume.weight = 1f;
            volume.sharedProfile = profile;
        }

        static VolumeProfile CreateProfile()
        {
            AssetDatabase.DeleteAsset(ProfilePath);
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            profile.name = "SparkCodeTemplePost";
            AssetDatabase.CreateAsset(profile, ProfilePath);

            var tone = Add<Tonemapping>(profile);
            tone.mode.Override(TonemappingMode.ACES);

            var color = Add<ColorAdjustments>(profile);
            color.postExposure.Override(0.34f);
            color.contrast.Override(17f);
            color.saturation.Override(10f);
            color.colorFilter.Override(new Color(1f, 0.975f, 0.93f, 1f));

            var wb = Add<WhiteBalance>(profile);
            wb.temperature.Override(5f);
            wb.tint.Override(1f);

            var bloom = Add<Bloom>(profile);
            bloom.threshold.Override(0.95f);
            bloom.intensity.Override(1.42f);
            bloom.scatter.Override(0.72f);
            bloom.tint.Override(new Color(1f, 0.83f, 0.54f, 1f));
            bloom.highQualityFiltering.Override(true);
            bloom.skipIterations.Override(1);

            var vignette = Add<Vignette>(profile);
            vignette.intensity.Override(0.33f);
            vignette.smoothness.Override(0.45f);
            vignette.color.Override(new Color(0.025f, 0.025f, 0.032f, 1f));

            var grain = Add<FilmGrain>(profile);
            grain.type.Override(FilmGrainLookup.Thin1);
            grain.intensity.Override(0.07f);
            grain.response.Override(0.65f);

            var ca = Add<ChromaticAberration>(profile);
            ca.intensity.Override(0.015f);

            var distortion = Add<LensDistortion>(profile);
            distortion.intensity.Override(-0.015f);
            distortion.scale.Override(1f);

            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();
            return profile;
        }

        static T Add<T>(VolumeProfile profile) where T : VolumeComponent
        {
            T component = profile.Add<T>(true);
            if (!AssetDatabase.Contains(component)) AssetDatabase.AddObjectToAsset(component, profile);
            return component;
        }

        static void BuildReflectionProbe(Transform parent)
        {
            var go = new GameObject("Temple Reflection Probe");
            go.transform.SetParent(parent, false);
            go.transform.position = new Vector3(0f, 8f, 0f);
            var probe = go.AddComponent<ReflectionProbe>();
            probe.mode = ReflectionProbeMode.Realtime;
            probe.refreshMode = ReflectionProbeRefreshMode.OnAwake;
            probe.timeSlicingMode = ReflectionProbeTimeSlicingMode.AllFacesAtOnce;
            probe.resolution = 256;
            probe.hdr = true;
            probe.clearFlags = ReflectionProbeClearFlags.Skybox;
            probe.size = new Vector3(32f, 25f, 32f);
            probe.center = new Vector3(0f, 3f, 0f);
            probe.importance = 1;
            probe.intensity = 0.9f;
            probe.RenderProbe();
        }

        // ------------------------------------------------------------------ capture

        // ------------------------------------------------------------------ diagnostics

        [MenuItem("SparkCode/诊断导入场景", false, 40)]
        public static void Diagnostics()
        {
            EditorSceneManager.OpenScene(ScenePath);
            Debug.Log("[Diag] render pipeline=" + GraphicsSettings.currentRenderPipeline);
            Debug.Log("[Diag] quality=" + QualitySettings.names[QualitySettings.GetQualityLevel()] +
                      " pipeline=" + QualitySettings.renderPipeline);

            var hero = GameObject.Find("CAM_Hero");
            if (hero != null)
            {
                var cam = hero.GetComponent<Camera>();
                var data = hero.GetComponent<UniversalAdditionalCameraData>();
                Debug.Log("[Diag] hero cam fov=" + cam.fieldOfView + " hdr=" + cam.allowHDR +
                          " post=" + (data != null ? data.renderPostProcessing.ToString() : "NULL") +
                          " aa=" + (data != null ? data.antialiasing.ToString() : "-") +
                          " mask=" + (data != null ? data.volumeLayerMask.value.ToString() : "-"));
            }

            var volume = UnityEngine.Object.FindObjectOfType<Volume>();
            if (volume == null)
            {
                Debug.LogError("[Diag] NO VOLUME");
            }
            else
            {
                Debug.Log("[Diag] volume global=" + volume.isGlobal + " weight=" + volume.weight +
                          " profile=" + (volume.sharedProfile != null ? volume.sharedProfile.name : "NULL"));
                if (volume.sharedProfile != null)
                {
                    foreach (var c in volume.sharedProfile.components)
                        Debug.Log("[Diag] volume component: " + (c != null ? c.GetType().Name + " active=" + c.active : "null"));
                    if (volume.sharedProfile.TryGet(out Bloom bloom))
                        Debug.Log("[Diag] bloom active=" + bloom.active + " threshold=" + bloom.threshold.value +
                                  " intensity=" + bloom.intensity.value + " scatter=" + bloom.scatter.value +
                                  " tint=" + bloom.tint.value);
                    if (volume.sharedProfile.TryGet(out Tonemapping tone))
                        Debug.Log("[Diag] tonemap=" + tone.mode.value);
                }
            }

            var star = GameObject.Find("Spark Pivot");
            if (star != null)
            {
                Debug.Log("[Diag] star pivot pos=" + star.transform.position + " children=" + star.transform.childCount);
                foreach (var r in star.GetComponentsInChildren<MeshRenderer>())
                {
                    var mf = r.GetComponent<MeshFilter>();
                    var mesh = mf != null ? mf.sharedMesh : null;
                    string mats = string.Join(", ", r.sharedMaterials.Select(m => m != null ? m.name + "[" + m.shader.name + "]" : "null"));
                    Debug.Log("[Diag] star r=" + r.name + " mats=" + mats +
                              " verts=" + (mesh != null ? mesh.vertexCount : 0) +
                              " uv=" + (mesh != null ? mesh.uv.Length : 0) +
                              " tangents=" + (mesh != null ? mesh.tangents.Length : 0) +
                              " bounds=" + r.bounds.size);
                }
            }

            var water = GameObject.Find("Pool_Water");
            if (water != null)
            {
                var r = water.GetComponent<MeshRenderer>();
                var mf = water.GetComponent<MeshFilter>();
                Debug.Log("[Diag] water mat=" + (r != null && r.sharedMaterial != null ? r.sharedMaterial.name : "NULL") +
                          " verts=" + (mf != null && mf.sharedMesh != null ? mf.sharedMesh.vertexCount : 0) +
                          " uv=" + (mf != null && mf.sharedMesh != null ? mf.sharedMesh.uv.Length : 0));
            }

            var ctrl = UnityEngine.Object.FindObjectOfType<TempleShowcaseController>();
            if (ctrl != null)
            {
                string camNames = ctrl.cameras == null ? "NULL" :
                    string.Join(", ", ctrl.cameras.Select(c => c != null ? c.name : "null"));
                Debug.Log("[Diag] controller cams=[" + camNames + "] starPivot=" +
                          (ctrl.starPivot != null ? ctrl.starPivot.name : "NULL"));
            }
            else Debug.LogError("[Diag] NO SHOWCASE CONTROLLER");

            int missingScripts = 0, nullMats = 0;
            foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>(true))
            {
                foreach (var mb in t.GetComponents<MonoBehaviour>()) if (mb == null) missingScripts++;
                var r = t.GetComponent<MeshRenderer>();
                if (r == null) continue;
                foreach (var m in r.sharedMaterials) if (m == null) nullMats++;
            }
            Debug.Log("[Diag] missingScripts=" + missingScripts + " nullMaterialSlots=" + nullMats);
        }

        public static void Capture()
        {            EditorSceneManager.OpenScene(ScenePath);
            int quality = ShaderPreviewTest.FindQuality("High Fidelity");
            if (quality >= 0) QualitySettings.SetQualityLevel(quality, true);

            var shots = new[]
            {
                new Shot("CAM_Hero", "unity_temple_hero.png"),
                new Shot("CAM_Oculus", "unity_temple_oculus.png"),
                new Shot("CAM_Star", "unity_temple_star.png"),
                new Shot("CAM_Wide", "unity_temple_wide.png")
            };

            Directory.CreateDirectory("Screenshots");
            foreach (var shot in shots)
            {
                foreach (var ps in UnityEngine.Object.FindObjectsOfType<ParticleSystem>(true))
                {
                    if (ps != null) ps.Simulate(5.0f, true, true);
                }

                var go = GameObject.Find(shot.CameraName);
                if (go == null) { Debug.LogWarning("[SparkCodeTempleImport] missing camera " + shot.CameraName); continue; }
                var cam = go.GetComponent<Camera>();
                foreach (var c in UnityEngine.Object.FindObjectsOfType<Camera>()) c.enabled = c == cam;
                cam.enabled = true;

                const int w = 1920, h = 1080;
                var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
                {
                    name = "Capture_" + shot.FileName
                };
                rt.Create();
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply();
                File.WriteAllBytes(Path.Combine("Screenshots", shot.FileName), tex.EncodeToPNG());
                RenderTexture.active = null;
                cam.targetTexture = null;
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
                UnityEngine.Object.DestroyImmediate(tex);
                Debug.Log("[SparkCodeTempleImport] captured " + shot.FileName);
            }

            AssetDatabase.Refresh();
        }

        struct Shot
        {
            public string CameraName;
            public string FileName;

            public Shot(string camera, string file)
            {
                CameraName = camera;
                FileName = file;
            }
        }

        // ------------------------------------------------------------------ helpers / logging

        static Bounds CalculateBounds(GameObject go)
        {
            var renderers = go.GetComponentsInChildren<MeshRenderer>(true);
            if (renderers.Length == 0) return new Bounds(Vector3.zero, Vector3.zero);
            var b = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) b.Encapsulate(renderers[i].bounds);
            return b;
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent))
                EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }

        static void AddSceneToBuildSettings(string scenePath)
        {
            var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            if (scenes.Any(s => s.path == scenePath)) return;
            scenes.Add(new EditorBuildSettingsScene(scenePath, false));
            EditorBuildSettings.scenes = scenes.ToArray();
        }

        static void LogStats()
        {
            var filters = UnityEngine.Object.FindObjectsOfType<MeshFilter>();
            int tris = 0;
            var ids = new HashSet<int>();
            foreach (var mf in filters)
            {
                if (mf.sharedMesh == null || !ids.Add(mf.sharedMesh.GetInstanceID())) continue;
                tris += mf.sharedMesh.triangles.Length / 3;
            }
            var renderers = UnityEngine.Object.FindObjectsOfType<MeshRenderer>();
            Debug.Log("[SparkCodeTempleImport] unique meshes=" + ids.Count + " renderers=" + renderers.Length +
                      " triangles=" + tris);
        }
    }
}
