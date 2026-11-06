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
    /// <summary>Imports BlenderStarTemple.fbx into a new, fully realtime URP scene.</summary>
    public static class StarTempleURPImport
    {
        public const string ModelPath = "Assets/Art/Models/BlenderStarTemple.fbx";
        public const string ScenePath = "Assets/Scenes/BlenderStarTemple_URP.unity";
        const string Generated = "Assets/Generated/BlenderStarTempleURP";
        static readonly Vector3 StarPosition = new Vector3(0f, 5.75f, 0f);
        static Material white, trim, dark, floor, basin, gold, water, sky, halo, shaft;
        static Material[] facets;
        static Material sparkGold, sparkTeal, sparkRose, trailGold, trailChampagne;

        [MenuItem("SparkCode/导入新版 Blender 神殿 (URP 实时光)", false, 50)]
        public static void BuildAndCapture() { Build(); Capture(); }

        public static void Build()
        {
            try
            {
                if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
                EnsureFolder(Generated);
                int quality = ShaderPreviewTest.FindQuality("High Fidelity");
                if (quality >= 0) QualitySettings.SetQualityLevel(quality, true);
                EnsureModelImportSettings();
                AssetDatabase.ImportAsset(ModelPath, ImportAssetOptions.ForceSynchronousImport);
                PrepareMaterials();

                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var lighting = new LightingSettings { name = "StarTemple_URP_Realtime", bakedGI = false, realtimeGI = false };
                AssetDatabase.CreateAsset(lighting, Generated + "/StarTemple_Realtime.lighting");
                Lightmapping.lightingSettings = lighting;

                var root = new GameObject("BlenderStarTemple_URP");
                var asset = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
                if (asset == null) throw new InvalidOperationException("FBX import failed: " + ModelPath);
                var model = PrefabUtility.InstantiatePrefab(asset) as GameObject;
                if (model == null) model = UnityEngine.Object.Instantiate(asset);
                model.name = "Temple_BlenderStarTemple";
                model.transform.SetParent(root.transform, false);
                model.transform.localPosition = Vector3.zero;
                if (PrefabUtility.IsPartOfPrefabInstance(model))
                    PrefabUtility.UnpackPrefabInstance(model, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);

                AssignMaterials(model);
                BuildWallBacking(root.transform);
                ConfigureEnvironment();
                BuildLights(root.transform);
                var star = GroupStar(model.transform, root.transform);
                var cameras = BuildCameras(root.transform);
                BuildAura(star);
                BuildShaft(root.transform);
                var controller = root.AddComponent<TempleShowcaseController>();
                controller.cameras = cameras;
                controller.starPivot = star;
                controller.starSpinSpeed = 2.0f;
                controller.starBobAmplitude = 0.10f;
                BuildPost();
                BuildReflectionProbe(root.transform);

                EditorSceneManager.MarkSceneDirty(scene);
                if (!EditorSceneManager.SaveScene(scene, ScenePath))
                    throw new InvalidOperationException("Could not save " + ScenePath);
                AddSceneToBuildSettings();
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                ValidateScene();
                Debug.Log("[StarTempleURP] BUILD COMPLETE: " + ScenePath);
            }
            catch (Exception e)
            {
                Debug.LogError("[StarTempleURP] BUILD FAILED: " + e);
                throw;
            }
        }

        static void EnsureModelImportSettings()
        {
            var importer = AssetImporter.GetAtPath(ModelPath) as ModelImporter;
            if (importer == null) throw new InvalidOperationException("No ModelImporter: " + ModelPath);
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
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null) throw new InvalidOperationException("Required material missing: " + path);
            return mat;
        }

        static Material Clone(Material source, string name)
        {
            string path = Generated + "/" + name + ".mat";
            AssetDatabase.DeleteAsset(path);
            var mat = new Material(source) { name = name };
            AssetDatabase.CreateAsset(mat, path);
            return mat;
        }

        static Material CloneDoubleSided(string sourcePath, string name)
        {
            var mat = Clone(Load(sourcePath), name);
            if (mat.HasProperty("_Cull")) mat.SetFloat("_Cull", 0f);
            return mat;
        }

        static Material Emissive(string name, Color color, float strength, float metallic = 0.2f)
        {
            string path = Generated + "/" + name + ".mat";
            AssetDatabase.DeleteAsset(path);
            var mat = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = name };
            mat.SetColor("_BaseColor", new Color(color.r * 0.35f, color.g * 0.35f, color.b * 0.35f, 1f));
            mat.SetFloat("_Metallic", metallic);
            mat.SetFloat("_Smoothness", 0.72f);
            if (mat.HasProperty("_Cull")) mat.SetFloat("_Cull", 0f);
            mat.SetColor("_EmissionColor", new Color(color.r * strength, color.g * strength, color.b * strength, 1f));
            mat.EnableKeyword("_EMISSION");
            mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            AssetDatabase.CreateAsset(mat, path);
            return mat;
        }

        static void PrepareMaterials()
        {
            white = CloneDoubleSided("Assets/Generated/Materials/Marble_White.mat", "Marble_White_SceneDouble");
            trim = CloneDoubleSided("Assets/Generated/Materials/Marble_Trim.mat", "Marble_Trim_SceneDouble");
            dark = CloneDoubleSided("Assets/Generated/Materials/Marble_Dark.mat", "Marble_Dark_SceneDouble");
            floor = CloneDoubleSided("Assets/Generated/Materials/Marble_Floor.mat", "Marble_Floor_SceneDouble");
            basin = CloneDoubleSided("Assets/Generated/Materials/Marble_Basin.mat", "Marble_Basin_SceneDouble");
            gold = CloneDoubleSided("Assets/Generated/Materials/Gold_Polished.mat", "Gold_SceneDouble");
            water = Clone(Load("Assets/Generated/Materials/Water_Ring.mat"), "Temple_Water_Realtime");
            if (water.HasProperty("_ShallowColor")) water.SetColor("_ShallowColor", new Color(0.035f, 0.20f, 0.19f));
            if (water.HasProperty("_DeepColor")) water.SetColor("_DeepColor", new Color(0.006f, 0.045f, 0.055f));
            if (water.HasProperty("_NormalStrength")) water.SetFloat("_NormalStrength", 0.32f);
            if (water.HasProperty("_Alpha")) water.SetFloat("_Alpha", 0.90f);
            if (water.HasProperty("_Glitter")) water.SetFloat("_Glitter", 0.12f);

            var spark = Load("Assets/Generated/Materials/Spark_Core.mat");
            var colors = new[]
            {
                new Color(1f, 0.50f, 0.08f), new Color(1f, 0.38f, 0.035f),
                new Color(1f, 0.65f, 0.16f), new Color(0.88f, 0.28f, 0.025f),
                new Color(1f, 0.56f, 0.10f)
            };
            facets = new Material[5];
            for (int i = 0; i < 5; i++)
            {
                facets[i] = Clone(spark, "Starfire_Facet_0" + (i + 1));
                facets[i].SetColor("_Color", colors[i]);
                facets[i].SetColor("_EmissionColor", colors[i]);
                facets[i].SetFloat("_EmissionIntensity", 2.0f + i * 0.15f);
                facets[i].SetFloat("_SparkleIntensity", 1.8f);
                facets[i].SetFloat("_SparkleScale", 16f + i * 2f);
                facets[i].SetFloat("_Pulse", 0.08f);
            }
            sparkGold = Emissive("Starfire_SparkGold", new Color(1f, 0.72f, 0.24f), 3.0f, 0.45f);
            sparkTeal = Emissive("Starfire_SparkTeal", new Color(0.08f, 0.85f, 0.62f), 3.0f);
            sparkRose = Emissive("Starfire_SparkRose", new Color(1f, 0.28f, 0.20f), 3.2f);
            trailGold = Emissive("Starfire_TrailGold", new Color(1f, 0.62f, 0.18f), 3.0f);
            trailChampagne = Emissive("Starfire_TrailChampagne", new Color(1f, 0.68f, 0.24f), 3.2f);
            sky = Clone(Load("Assets/Generated/Materials/Sky_Temple.mat"), "Temple_Sky_Realtime");
            halo = Clone(Load("Assets/Generated/Materials/Spark_Halo.mat"), "Starfire_Halo_Realtime");
            shaft = Clone(Load("Assets/Generated/Materials/Temple_LightShaft.mat"), "Temple_Shaft_Realtime");
            if (halo.HasProperty("_Intensity")) halo.SetFloat("_Intensity", 1.25f);
            if (shaft.HasProperty("_Intensity")) shaft.SetFloat("_Intensity", 0.70f);
            AssetDatabase.SaveAssets();
        }

        static void AssignMaterials(GameObject model)
        {
            int colliders = 0;
            foreach (var r in model.GetComponentsInChildren<MeshRenderer>(true))
            {
                string obj = r.gameObject.name.ToLowerInvariant();
                bool isStar = obj.StartsWith("starfire_");
                var mats = r.sharedMaterials;
                if (mats == null || mats.Length == 0) mats = new Material[1];
                for (int i = 0; i < mats.Length; i++)
                {
                    string key = mats[i] != null ? mats[i].name.ToLowerInvariant() : string.Empty;
                    Material target = white;
                    if (key.Contains("starfire_goldfacet"))
                    {
                        int index = 0;
                        int parsed;
                        if (int.TryParse(key.Substring(Mathf.Max(0, key.Length - 2)), out parsed)) index = Mathf.Clamp(parsed - 1, 0, 4);
                        target = facets[index];
                    }
                    else if (key.Contains("trailchampagne")) target = trailChampagne;
                    else if (key.Contains("trailgold")) target = trailGold;
                    else if (key.Contains("trailrose") || key.Contains("sparkrose")) target = sparkRose;
                    else if (key.Contains("sparkteal")) target = sparkTeal;
                    else if (key.Contains("sparkgold")) target = sparkGold;
                    else if (isStar) target = sparkGold;
                    else if (key.Contains("water") || obj.Contains("water")) target = water;
                    else if (key.Contains("pool") || key.Contains("mosaic") || obj.Contains("pool")) target = basin;
                    else if (key.Contains("floor") || obj.Contains("paving")) target = floor;
                    else if (key.Contains("aurum") || key.Contains("gold")) target = gold;
                    else if (key.Contains("shadow") || key.Contains("joint")) target = dark;
                    else if (key.Contains("pearl") || key.Contains("ivory")) target = trim;
                    mats[i] = target;
                }
                r.sharedMaterials = mats;
                r.shadowCastingMode = isStar || obj.Contains("water") ? ShadowCastingMode.Off : ShadowCastingMode.On;
                r.receiveShadows = !isStar && !obj.Contains("water");
                r.lightProbeUsage = isStar ? LightProbeUsage.Off : LightProbeUsage.BlendProbes;
                r.reflectionProbeUsage = isStar ? ReflectionProbeUsage.Off : ReflectionProbeUsage.BlendProbes;
                if (!isStar)
                {
                    GameObjectUtility.SetStaticEditorFlags(r.gameObject,
                        StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic);
                }
                bool collision = obj.Contains("paving") || obj.Contains("bridge") || obj.Contains("island") ||
                                 obj.Contains("twenty-flute tapered shaft") || obj.Contains("structural deck") ||
                                 obj.Contains("walkable foundation") || obj.Contains("coping");
                if (collision)
                {
                    var mf = r.GetComponent<MeshFilter>();
                    if (mf != null && mf.sharedMesh != null)
                    {
                        var mc = r.gameObject.GetComponent<MeshCollider>();
                        if (mc == null) mc = r.gameObject.AddComponent<MeshCollider>();
                        mc.sharedMesh = mf.sharedMesh;
                        mc.convex = false;
                        colliders++;
                    }
                }
            }
            Debug.Log("[StarTempleURP] materials assigned; mesh colliders=" + colliders);
        }

        static void BuildWallBacking(Transform parent)
        {
            // Dark, continuous mortar behind the individually jointed ashlar blocks.
            // It prevents bright sky pinholes without changing the visible wall face.
            const int seg = 160;
            const float inner = 17.095f, outer = 17.35f, bottom = 0.02f, top = 9.12f;
            var vertices = new List<Vector3>(seg * 4);
            var uv = new List<Vector2>(seg * 4);
            var triangles = new List<int>(seg * 24);
            for (int layer = 0; layer < 4; layer++)
            {
                float r = (layer % 2 == 0) ? inner : outer;
                float y = (layer < 2) ? bottom : top;
                for (int i = 0; i < seg; i++)
                {
                    float a = i * Mathf.PI * 2f / seg;
                    vertices.Add(new Vector3(r * Mathf.Cos(a), y, r * Mathf.Sin(a)));
                    uv.Add(new Vector2(i * 0.25f, y * 0.25f));
                }
            }
            for (int i = 0; i < seg; i++)
            {
                int j = (i + 1) % seg;
                void Quad(int a, int b, int c, int d)
                {
                    triangles.Add(a); triangles.Add(b); triangles.Add(c);
                    triangles.Add(a); triangles.Add(c); triangles.Add(d);
                }
                Quad(2 * seg + i, 2 * seg + j, 3 * seg + j, 3 * seg + i);
                Quad(i, seg + i, seg + j, j);
                Quad(seg + i, 3 * seg + i, 3 * seg + j, seg + j);
                Quad(i, 2 * seg + i, 2 * seg + j, j);
            }
            var mesh = new Mesh { name = "OuterWall_ContinuousMortar" };
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uv);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            AssetDatabase.DeleteAsset(Generated + "/OuterWall_Mortar.asset");
            AssetDatabase.CreateAsset(mesh, Generated + "/OuterWall_Mortar.asset");
            var go = new GameObject("Outer wall continuous dark mortar");
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = dark;
            renderer.shadowCastingMode = ShadowCastingMode.On;
            GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic);
        }

        static void ConfigureEnvironment()
        {
            RenderSettings.skybox = sky;
            if (sky.HasProperty("_ZenithColor")) sky.SetColor("_ZenithColor", new Color(0.10f, 0.22f, 0.44f));
            if (sky.HasProperty("_HorizonColor")) sky.SetColor("_HorizonColor", new Color(0.72f, 0.78f, 0.84f));
            if (sky.HasProperty("_GroundColor")) sky.SetColor("_GroundColor", new Color(0.24f, 0.23f, 0.21f));
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.20f, 0.27f, 0.38f);
            RenderSettings.ambientEquatorColor = new Color(0.34f, 0.31f, 0.27f);
            RenderSettings.ambientGroundColor = new Color(0.10f, 0.09f, 0.08f);
            RenderSettings.ambientIntensity = 0.80f;
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogDensity = 0.0045f;
            RenderSettings.fogColor = new Color(0.12f, 0.145f, 0.18f);
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Skybox;
            RenderSettings.reflectionIntensity = 0.75f;
        }

        static Light ConfigureLight(Light light, Color color, float intensity, float range, LightShadows shadows)
        {
            light.color = color;
            light.intensity = intensity;
            light.range = range;
            light.shadows = shadows;
            light.renderMode = LightRenderMode.ForcePixel;
            light.lightmapBakeType = LightmapBakeType.Realtime;
            light.shadowBias = 0.04f;
            light.shadowNormalBias = 0.30f;
            return light;
        }

        static Light Point(Transform parent, string name, Vector3 pos, Color color, float intensity, float range, LightShadows shadows = LightShadows.None)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.position = pos;
            var light = go.AddComponent<Light>();
            light.type = LightType.Point;
            return ConfigureLight(light, color, intensity, range, shadows);
        }

        static Light Spot(Transform parent, string name, Vector3 pos, Vector3 target, Color color, float intensity, float range, float angle, LightShadows shadows)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.SetPositionAndRotation(pos, Quaternion.LookRotation((target - pos).normalized, Vector3.up));
            var light = go.AddComponent<Light>();
            light.type = LightType.Spot;
            light.spotAngle = angle;
            light.innerSpotAngle = angle * 0.60f;
            return ConfigureLight(light, color, intensity, range, shadows);
        }

        static void BuildLights(Transform parent)
        {
            var sunGo = new GameObject("Sun_Oculus_Realtime");
            sunGo.transform.SetParent(parent, false);
            sunGo.transform.rotation = Quaternion.LookRotation(new Vector3(-3f, -20f, -3f).normalized, Vector3.up);
            var sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            ConfigureLight(sun, new Color(1f, 0.88f, 0.70f), 2.8f, 0f, LightShadows.Soft);
            sun.shadowStrength = 0.92f;
            RenderSettings.sun = sun;
            if (sky.HasProperty("_SunDirection"))
            {
                var d = -sunGo.transform.forward;
                sky.SetVector("_SunDirection", new Vector4(d.x, d.y, d.z, 0f));
            }

            var fillGo = new GameObject("Cool_Fill_Realtime");
            fillGo.transform.SetParent(parent, false);
            fillGo.transform.rotation = Quaternion.LookRotation(new Vector3(-0.35f, -0.55f, -0.76f).normalized, Vector3.up);
            var fill = fillGo.AddComponent<Light>();
            fill.type = LightType.Directional;
            ConfigureLight(fill, new Color(0.60f, 0.72f, 0.92f), 0.32f, 0f, LightShadows.None);

            Spot(parent, "Oculus_Spot_Realtime", new Vector3(0f, 18.65f, 1f), new Vector3(-3f, 0f, -2f),
                new Color(1f, 0.88f, 0.68f), 13f, 28f, 24f, LightShadows.Soft);
            Point(parent, "Dome_Fill", new Vector3(0f, 12f, 0f), new Color(0.52f, 0.66f, 0.95f), 2.6f, 24f);
            Point(parent, "Pool_Fill", new Vector3(0f, 1.2f, 0f), new Color(0.22f, 0.72f, 0.66f), 0.9f, 9f);
            Point(parent, "Front_Marble_Fill", new Vector3(0f, 6.5f, 6.8f), new Color(1f, 0.85f, 0.68f), 1.7f, 18f);
            Point(parent, "Spark_Key_Realtime", StarPosition + new Vector3(0f, -0.3f, 0f), new Color(1f, 0.52f, 0.16f), 5.0f, 13f, LightShadows.Soft);
            Point(parent, "Spark_Rim", StarPosition + new Vector3(1.5f, 0.8f, -0.4f), new Color(1f, 0.30f, 0.06f), 1.0f, 6f);
            Point(parent, "Spark_TealAccent", StarPosition + new Vector3(-1.6f, -0.7f, 0.3f), new Color(0.10f, 0.82f, 0.61f), 0.55f, 5f);
            for (int i = 0; i < 4; i++)
            {
                float a = (45f + i * 90f) * Mathf.Deg2Rad;
                var pos = new Vector3(13.3f * Mathf.Cos(a), 3.0f, 13.3f * Mathf.Sin(a));
                Point(parent, "Outer_Aisle_Fill_" + (i + 1), pos, new Color(0.60f, 0.72f, 0.92f), 1.4f, 11f);
            }
            for (int i = 0; i < 4; i++)
            {
                float a = (22.5f + i * 90f) * Mathf.Deg2Rad;
                var pos = new Vector3(8.6f * Mathf.Cos(a), 7.3f, 8.6f * Mathf.Sin(a));
                Spot(parent, "Dome_Uplight_" + (i + 1), pos, new Vector3(0f, 16f, 0f),
                    new Color(1f, 0.82f, 0.62f), 1.8f, 19f, 58f, LightShadows.None);
            }
        }

        static Transform GroupStar(Transform model, Transform root)
        {
            var pivot = new GameObject("Spark Pivot").transform;
            pivot.SetParent(root, false);
            pivot.position = StarPosition;
            var objects = new List<Transform>();
            foreach (var t in model.GetComponentsInChildren<Transform>(true))
                if (t != model && t.name.StartsWith("Starfire_")) objects.Add(t);
            foreach (var t in objects)
            {
                if (t.parent != null && objects.Contains(t.parent)) continue;
                t.SetParent(pivot, true);
            }
            Debug.Log("[StarTempleURP] grouped star objects=" + objects.Count);
            return pivot;
        }

        static GameObject Additive(string name, Mesh mesh, Material material, Transform parent, Vector3 position, float scale)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = position;
            go.transform.localScale = Vector3.one * scale;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            return go;
        }

        static void BuildAura(Transform star)
        {
            var quad = AssetDatabase.LoadAssetAtPath<Mesh>("Assets/Generated/Meshes/Spark_HaloQuad.asset");
            if (quad == null) quad = AssetDatabase.LoadAssetAtPath<Mesh>("Assets/Generated/Meshes/Spark_GlowQuad.asset");
            if (quad == null) return;
            for (int i = 0; i < 2; i++)
            {
                var go = Additive("Starfire Halo " + i, quad, halo, star, Vector3.zero, i == 0 ? 6.5f : 4.0f);
                var bill = go.AddComponent<Billboard>();
                bill.rollSpeed = i == 0 ? 1.4f : -2.1f;
                bill.baseRoll = i * 27f;
                bill.cameraTransform = GameObject.Find("CAM_Hero")?.transform;
            }
        }

        static void BuildShaft(Transform root)
        {
            var mesh = SparkStarMesh.CreateShaft("BlenderStarTemple_Shaft",
                new Vector3(0f, 18.65f, 1f), new Vector3(-3f, 0.10f, -2f),
                0.24f, 2.55f, 48, 8, 0.18f, 0.025f,
                new[] { 0.60f, 1.0f }, new[] { 0.45f, 0.16f });
            AssetDatabase.DeleteAsset(Generated + "/LightShaft.asset");
            AssetDatabase.CreateAsset(mesh, Generated + "/LightShaft.asset");
            Additive("Oculus Light Shaft (URP approximation)", mesh, shaft, root, Vector3.zero, 1f);
        }

        static Camera MakeCamera(string name, Transform parent, Vector3 pos, Vector3 target, float verticalFov, bool main)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.SetPositionAndRotation(pos, Quaternion.LookRotation((target - pos).normalized, Vector3.up));
            var cam = go.AddComponent<Camera>();
            cam.fieldOfView = verticalFov;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 220f;
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.allowHDR = true;
            cam.allowMSAA = true;
            cam.enabled = main;
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
            return cam;
        }

        static Camera[] BuildCameras(Transform parent)
        {
            return new[]
            {
                MakeCamera("CAM_Hero", parent, new Vector3(0f, 2.75f, 10.4f), new Vector3(0f, 6.3f, 0f), 82f, true),
                MakeCamera("CAM_Oculus", parent, new Vector3(0.4f, 1.5f, 3.5f), new Vector3(0f, 13.7f, 0f), 74f, false),
                MakeCamera("CAM_Star", parent, new Vector3(4.6f, 3.4f, 6.4f), new Vector3(0f, 4.8f, 0f), 51f, false),
                MakeCamera("CAM_Aisle", parent, new Vector3(5.65f, 2.3f, 12.22f), new Vector3(13f, 3f, -1.5f), 57f, false)
            };
        }

        static T Add<T>(VolumeProfile profile) where T : VolumeComponent
        {
            var component = profile.Add<T>(true);
            if (!AssetDatabase.Contains(component)) AssetDatabase.AddObjectToAsset(component, profile);
            return component;
        }

        static void BuildPost()
        {
            string path = Generated + "/StarTemple_Post.asset";
            AssetDatabase.DeleteAsset(path);
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            profile.name = "StarTemple_Post";
            AssetDatabase.CreateAsset(profile, path);
            Add<Tonemapping>(profile).mode.Override(TonemappingMode.ACES);
            var color = Add<ColorAdjustments>(profile);
            color.postExposure.Override(0.20f);
            color.contrast.Override(12f);
            color.saturation.Override(6f);
            var bloom = Add<Bloom>(profile);
            bloom.threshold.Override(1.0f);
            bloom.intensity.Override(1.15f);
            bloom.scatter.Override(0.68f);
            bloom.highQualityFiltering.Override(true);
            var vignette = Add<Vignette>(profile);
            vignette.intensity.Override(0.27f);
            vignette.smoothness.Override(0.45f);
            var go = new GameObject("Global Volume (URP Bloom)");
            var volume = go.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.sharedProfile = profile;
            volume.priority = 20f;
        }

        static void BuildReflectionProbe(Transform parent)
        {
            var go = new GameObject("Temple Realtime Reflection Probe");
            go.transform.SetParent(parent, false);
            go.transform.position = new Vector3(0f, 5.0f, 0f);
            var probe = go.AddComponent<ReflectionProbe>();
            probe.mode = ReflectionProbeMode.Realtime;
            probe.refreshMode = ReflectionProbeRefreshMode.EveryFrame;
            probe.timeSlicingMode = ReflectionProbeTimeSlicingMode.IndividualFaces;
            probe.resolution = 256;
            probe.hdr = true;
            probe.clearFlags = ReflectionProbeClearFlags.Skybox;
            probe.size = new Vector3(40f, 26f, 40f);
            probe.center = new Vector3(0f, 4f, 0f);
            probe.intensity = 0.9f;
        }

        public static void Capture()
        {
            EditorSceneManager.OpenScene(ScenePath);
            int quality = ShaderPreviewTest.FindQuality("High Fidelity");
            if (quality >= 0) QualitySettings.SetQualityLevel(quality, true);
            Directory.CreateDirectory("Screenshots");
            var shots = new[]
            {
                new Shot("CAM_Hero", "BlenderStarTemple_Hero.png", 1600, 1200),
                new Shot("CAM_Oculus", "BlenderStarTemple_Oculus.png", 1600, 1200),
                new Shot("CAM_Star", "BlenderStarTemple_Star.png", 1440, 1440),
                new Shot("CAM_Aisle", "BlenderStarTemple_Aisle.png", 1600, 1120)
            };
            foreach (var shot in shots)
            {
                var go = GameObject.Find(shot.CameraName);
                if (go == null) throw new InvalidOperationException("Missing camera " + shot.CameraName);
                var cam = go.GetComponent<Camera>();
                foreach (var c in UnityEngine.Object.FindObjectsOfType<Camera>()) c.enabled = c == cam;
                cam.enabled = true;
                var rt = new RenderTexture(shot.Width, shot.Height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
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
                UnityEngine.Object.DestroyImmediate(rt);
                UnityEngine.Object.DestroyImmediate(tex);
                Debug.Log("[StarTempleURP] captured " + shot.FileName);
            }
            AssetDatabase.Refresh();
        }

        [MenuItem("SparkCode/验证新版神殿 URP 场景", false, 52)]
        public static void ValidateScene()
        {
            var report = new ValidationReport();
            report.scene = ScenePath;
            report.pipeline = GraphicsSettings.currentRenderPipeline != null ? GraphicsSettings.currentRenderPipeline.name : "Built-in";
            var model = GameObject.Find("Temple_BlenderStarTemple");
            if (model == null) throw new InvalidOperationException("Imported model root missing");
            var renderers = UnityEngine.Object.FindObjectsOfType<MeshRenderer>();
            report.renderers = renderers.Length;
            report.lights = UnityEngine.Object.FindObjectsOfType<Light>().Length;
            report.realtimeLights = UnityEngine.Object.FindObjectsOfType<Light>().Count(l => l.lightmapBakeType == LightmapBakeType.Realtime);
            report.meshColliders = UnityEngine.Object.FindObjectsOfType<MeshCollider>().Length;
            report.cameras = UnityEngine.Object.FindObjectsOfType<Camera>().Length;
            report.starMeshes = GameObject.Find("Spark Pivot")?.GetComponentsInChildren<MeshRenderer>().Length ?? 0;
            var ids = new HashSet<int>();
            int triangles = 0;
            var bounds = new Bounds();
            bool first = true;
            foreach (var r in renderers)
            {
                if (first) { bounds = r.bounds; first = false; } else bounds.Encapsulate(r.bounds);
                foreach (var m in r.sharedMaterials) if (m == null) report.nullMaterialSlots++;
                var mf = r.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null && ids.Add(mf.sharedMesh.GetInstanceID())) triangles += mf.sharedMesh.triangles.Length / 3;
            }
            report.uniqueMeshes = ids.Count;
            report.uniqueTriangles = triangles;
            report.bounds = bounds.size.ToString("F3");
            foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>(true))
                foreach (var mb in t.GetComponents<MonoBehaviour>()) if (mb == null) report.missingScripts++;
            var volume = UnityEngine.Object.FindObjectOfType<Volume>();
            report.hasBloom = volume != null && volume.sharedProfile != null && volume.sharedProfile.Has<Bloom>();
            report.urpActive = GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset;
            report.passed = report.urpActive && report.renderers >= 500 && report.realtimeLights >= 10 &&
                            report.nullMaterialSlots == 0 && report.missingScripts == 0 && report.hasBloom && report.starMeshes >= 60;
            Directory.CreateDirectory("Docs");
            File.WriteAllText("Docs/BlenderStarTemple_UnityValidation.json", JsonUtility.ToJson(report, true));
            Debug.Log("[StarTempleURP] VALIDATION " + JsonUtility.ToJson(report));
            if (!report.passed) throw new InvalidOperationException("Scene validation failed; see Docs/BlenderStarTemple_UnityValidation.json");
        }

        static void AddSceneToBuildSettings()
        {
            var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            if (scenes.Any(s => s.path == ScenePath)) return;
            scenes.Add(new EditorBuildSettingsScene(ScenePath, false));
            EditorBuildSettings.scenes = scenes.ToArray();
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }

        struct Shot
        {
            public string CameraName, FileName;
            public int Width, Height;
            public Shot(string camera, string file, int width, int height) { CameraName = camera; FileName = file; Width = width; Height = height; }
        }

        [Serializable]
        public class ValidationReport
        {
            public string scene, pipeline, bounds;
            public bool urpActive, hasBloom, passed;
            public int renderers, lights, realtimeLights, cameras, starMeshes, meshColliders;
            public int uniqueMeshes, uniqueTriangles, nullMaterialSlots, missingScripts;
        }
    }
}
