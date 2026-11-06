using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace SparkCode.Generation
{
    /// <summary>
    /// Editor-side asset factory shared by the environment, spark and menu builders.
    /// Creates and persists every material/mesh so the saved scene has no missing references.
    /// </summary>
    public class TempleBuildKit
    {
        public Transform Root { get; private set; }

        public Texture2D MarbleAlbedo, MarbleNormal, FloorAlbedo, FloorNormal, WaterNormal;
        public Texture2D SparkHalo, SparkMote, SoftGlow, SparkSurfaceAlbedo, SparkSurfaceEmissive;

        public Material Marble;
        public Material MarbleFloor;
        public Material MarbleTrim;
        public Material MarbleDark;
        public Material MarbleBasin;
        public Material Gold;
        public Material Water;
        public Material SparkCore;
        public Material SparkHaloMat;
        public Material SparkMoteMat;
        public Material LightShaftMat;
        public Material DustMat;

        readonly Dictionary<string, Sprite> _sprites = new Dictionary<string, Sprite>();

        public const string GeneratedRoot = "Assets/Generated";
        public const string MaterialRoot = "Assets/Generated/Materials";
        public const string MeshRoot = "Assets/Generated/Meshes";
        public const string TextureRoot = "Assets/Art/Textures";

        public TempleBuildKit(Transform root)
        {
            Root = root;
            EnsureFolders();
            LoadTextures();
            CreateMaterials();
        }

        static void EnsureFolders()
        {
            EnsureFolder("Assets", "Generated");
            EnsureFolder("Assets/Generated", "Materials");
            EnsureFolder("Assets/Generated", "Meshes");
            EnsureFolder("Assets", "Art");
            EnsureFolder("Assets/Art", "Textures");
            EnsureFolder("Assets/Art", "Shaders");
        }

        public static void EnsureFolder(string parent, string name)
        {
            string path = parent + "/" + name;
            if (!AssetDatabase.IsValidFolder(path)) AssetDatabase.CreateFolder(parent, name);
        }

        // ------------------------------------------------------------------ textures
        void LoadTextures()
        {
            MarbleAlbedo = LoadTexture(TextureRoot + "/MarbleWhite_Albedo.png", false, false);
            MarbleNormal = LoadTexture(TextureRoot + "/MarbleWhite_Normal.png", true, true);
            FloorAlbedo = LoadTexture(TextureRoot + "/FloorTiles_Albedo.png", false, false);
            FloorNormal = LoadTexture(TextureRoot + "/FloorTiles_Normal.png", true, true);
            WaterNormal = LoadTexture(TextureRoot + "/Water_Normal.png", true, true);
            SparkHalo = LoadTexture(TextureRoot + "/SparkHalo.png", false, true);
            SparkMote = LoadTexture(TextureRoot + "/SparkMote.png", false, true);
            SoftGlow = LoadTexture(TextureRoot + "/SoftGlow.png", false, true);
            SparkSurfaceAlbedo = LoadTexture(TextureRoot + "/SparkSurface_Albedo.png", false, false);
            SparkSurfaceEmissive = LoadTexture(TextureRoot + "/SparkSurface_Emissive.png", false, false);
        }

        public Texture2D LoadTexture(string assetPath, bool normalMap, bool clamp)
        {
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
            if (tex == null)
            {
                Debug.LogWarning("[TempleBuildKit] Missing texture: " + assetPath);
                return null;
            }
            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer != null)
            {
                bool dirty = false;
                if (importer.textureType != (normalMap ? TextureImporterType.NormalMap : TextureImporterType.Default))
                {
                    importer.textureType = normalMap ? TextureImporterType.NormalMap : TextureImporterType.Default;
                    dirty = true;
                }
                if (importer.sRGBTexture == normalMap) { importer.sRGBTexture = !normalMap; dirty = true; }
                if (importer.wrapMode != (clamp ? TextureWrapMode.Clamp : TextureWrapMode.Repeat))
                {
                    importer.wrapMode = clamp ? TextureWrapMode.Clamp : TextureWrapMode.Repeat;
                    dirty = true;
                }
                if (importer.alphaIsTransparency != (clamp && !normalMap))
                {
                    importer.alphaIsTransparency = clamp && !normalMap;
                    dirty = true;
                }
                if (importer.maxTextureSize < 2048) { importer.maxTextureSize = 2048; dirty = true; }
                if (importer.mipmapEnabled != true) { importer.mipmapEnabled = true; dirty = true; }
                if (importer.anisoLevel < 8) { importer.anisoLevel = 8; dirty = true; }
                if (dirty) importer.SaveAndReimport();
            }
            return tex;
        }

        public Sprite LoadSprite(string assetPath)
        {
            if (_sprites.TryGetValue(assetPath, out var cached) && cached != null) return cached;
            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(assetPath);
            if (sprite == null)
            {
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
                if (tex == null)
                {
                    Debug.LogError("[TempleBuildKit] Missing UI sprite: " + assetPath);
                    return null;
                }
                var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
                if (importer != null)
                {
                    importer.textureType = TextureImporterType.Sprite;
                    importer.spriteImportMode = SpriteImportMode.Single;
                    importer.alphaIsTransparency = true;
                    importer.mipmapEnabled = false;
                    importer.wrapMode = TextureWrapMode.Clamp;
                    importer.filterMode = FilterMode.Bilinear;
                    importer.spritePixelsPerUnit = 100f;
                    importer.maxTextureSize = 2048;
                    importer.SaveAndReimport();
                }
                sprite = AssetDatabase.LoadAssetAtPath<Sprite>(assetPath);
            }
            _sprites[assetPath] = sprite;
            return sprite;
        }

        // ------------------------------------------------------------------ materials
        void CreateMaterials()
        {
            Marble = MakeLit("Marble_White", MarbleAlbedo, MarbleNormal, Color.white, 0.035f, 0.62f, new Vector2(1f, 1f));
            MarbleFloor = MakeLit("Marble_Floor", FloorAlbedo, FloorNormal, new Color(0.98f, 0.97f, 0.94f), 0.02f, 0.56f, new Vector2(1f, 1f));
            MarbleTrim = MakeLit("Marble_Trim", MarbleAlbedo, MarbleNormal, new Color(0.94f, 0.88f, 0.76f), 0.05f, 0.6f, new Vector2(2f, 2f));
            MarbleDark = MakeLit("Marble_Dark", MarbleAlbedo, MarbleNormal, new Color(0.36f, 0.325f, 0.3f), 0.0f, 0.34f, new Vector2(1.5f, 1.5f));
            MarbleBasin = MakeLit("Marble_Basin", MarbleAlbedo, MarbleNormal, new Color(0.62f, 0.63f, 0.58f), 0.0f, 0.8f, new Vector2(2f, 2f));
            Gold = MakeLit("Gold_Polished", null, null, new Color(0.85f, 0.62f, 0.25f), 0.92f, 0.86f, Vector2.one);
            Gold.SetFloat("_Smoothness", 0.9f);

            var waterShader = Shader.Find("SparkCode/Water");
            Water = NewMaterial("Water_Ring", waterShader);
            if (Water != null)
            {
                Water.SetColor("_ShallowColor", new Color(0.20f, 0.46f, 0.47f, 1f));
                Water.SetColor("_DeepColor", new Color(0.035f, 0.13f, 0.15f, 1f));
                Water.SetColor("_FoamColor", new Color(0.74f, 0.94f, 0.92f, 1f));
                Water.SetColor("_FresnelColor", new Color(0.68f, 0.8f, 0.86f, 1f));
                Water.SetFloat("_NormalStrength", 0.6f);
                Water.SetFloat("_FlowSpeed", 0.055f);
                Water.SetFloat("_Smoothness", 0.93f);
                Water.SetFloat("_Alpha", 0.9f);
                if (WaterNormal != null) Water.SetTexture("_NormalMap", WaterNormal);
            }

            var sparkShader = Shader.Find("SparkCode/SparkCore");
            SparkCore = NewMaterial("Spark_Core", sparkShader);
            if (SparkCore != null)
            {
                if (SparkSurfaceAlbedo != null) SparkCore.SetTexture("_MainTex", SparkSurfaceAlbedo);
                if (SparkSurfaceEmissive != null) SparkCore.SetTexture("_EmissiveTex", SparkSurfaceEmissive);
                SparkCore.SetColor("_Color", new Color(1f, 0.72f, 0.28f, 1f));
                SparkCore.SetColor("_EmissionColor", new Color(1f, 0.74f, 0.32f, 1f));
                SparkCore.SetFloat("_EmissionIntensity", 5.2f);
                SparkCore.SetFloat("_FlowSpeed", 0.12f);
                SparkCore.SetColor("_FresnelColor", new Color(1f, 0.92f, 0.6f, 1f));
                SparkCore.SetFloat("_FresnelPower", 2.4f);
                SparkCore.SetFloat("_SparkleScale", 26f);
                SparkCore.SetFloat("_SparkleIntensity", 2.2f);
                SparkCore.SetFloat("_Pulse", 0.15f);
            }

            SparkHaloMat = CreateAdditiveMaterial("Spark_Halo", SparkHalo, new Color(1f, 0.72f, 0.28f), 2.4f, 0.75f);
            SparkMoteMat = CreateAdditiveMaterial("Spark_Mote", SparkMote, new Color(1f, 0.78f, 0.36f), 2.0f, 0.0f);
            DustMat = CreateAdditiveMaterial("Temple_Dust", SoftGlow, new Color(1f, 0.93f, 0.8f), 0.55f, 0.65f);
            LightShaftMat = CreateAdditiveMaterial("Temple_LightShaft", SoftGlow, new Color(1f, 0.91f, 0.72f), 0.5f, 0f);
        }

        /// <summary>Create + persist a material for any shader (used for the skybox etc.).</summary>
        public Material CreateMaterial(string name, string shaderName)
        {
            return NewMaterial(name, Shader.Find(shaderName));
        }

        Material MakeLit(string name, Texture2D albedo, Texture2D normal, Color color, float metallic, float smoothness, Vector2 tiling)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            var mat = NewMaterial(name, shader);
            if (mat == null) return null;
            mat.SetColor("_BaseColor", color);
            mat.SetFloat("_Metallic", metallic);
            mat.SetFloat("_Smoothness", smoothness);
            mat.SetVector("_BaseMap_ST", new Vector4(tiling.x, tiling.y, 0f, 0f));
            if (albedo != null)
            {
                mat.SetTexture("_BaseMap", albedo);
                mat.SetTexture("_MainTex", albedo);
            }
            if (normal != null)
            {
                mat.SetTexture("_BumpMap", normal);
                mat.EnableKeyword("_NORMALMAP");
                mat.SetFloat("_BumpScale", 0.7f);
            }
            mat.enableInstancing = true;
            return mat;
        }

        Material NewMaterial(string name, Shader shader)
        {
            if (shader == null)
            {
                Debug.LogError("[TempleBuildKit] Missing shader for " + name);
                return null;
            }
            var mat = new Material(shader) { name = name };
            string path = MaterialRoot + "/" + name + ".mat";
            AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(mat, path);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        public Material CreateAdditiveMaterial(string name, Texture2D tex, Color tint, float intensity, float softFade)
        {
            var shader = Shader.Find("SparkCode/AdditiveGlow");
            var mat = NewMaterial(name, shader);
            if (mat == null) return null;
            if (tex != null) mat.SetTexture("_MainTex", tex);
            mat.SetColor("_Color", tint);
            mat.SetFloat("_Intensity", intensity);
            mat.SetFloat("_SoftFade", softFade);
            mat.SetVector("_Scroll", Vector4.zero);
            mat.SetFloat("_Pulse", 0.12f);
            return mat;
        }

        // ------------------------------------------------------------------ objects
        public GameObject Create(string name, Transform parent = null)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent != null ? parent : Root, false);
            return go;
        }

        public Mesh SaveMesh(string name, Mesh mesh)
        {
            if (mesh == null) return null;
            string path = MeshRoot + "/" + name + ".asset";
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing != null) AssetDatabase.DeleteAsset(path);
            mesh.name = name;
            AssetDatabase.CreateAsset(mesh, path);
            return AssetDatabase.LoadAssetAtPath<Mesh>(path);
        }

        public GameObject CreateMeshObject(string name, Mesh mesh, Material mat, Transform parent = null, bool saveMesh = true)
        {
            if (saveMesh) mesh = SaveMesh(name, mesh);
            var go = new GameObject(name);
            go.transform.SetParent(parent != null ? parent : Root, false);
            if (mesh != null)
            {
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = go.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = mat;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                renderer.receiveShadows = true;
                renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
                renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            }
            return go;
        }

        public void AddMeshCollider(GameObject go, bool convex = false)
        {
            var mf = go.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) return;
            var col = go.AddComponent<MeshCollider>();
            col.sharedMesh = mf.sharedMesh;
            col.convex = convex;
        }

        public void AddBoxCollider(GameObject go, Vector3 center, Vector3 size)
        {
            var col = go.AddComponent<BoxCollider>();
            col.center = center;
            col.size = size;
        }

        public static void MarkStatic(GameObject go)
        {
            if (go == null) return;
            go.isStatic = true;
            foreach (Transform t in go.transform) MarkStatic(t.gameObject);
        }

        public void SetLayerRecursively(GameObject go, int layer)
        {
            if (go == null) return;
            go.layer = layer;
            foreach (Transform t in go.transform) SetLayerRecursively(t.gameObject, layer);
        }

        public string EnsureSceneFolder()
        {
            if (!AssetDatabase.IsValidFolder("Assets/Scenes")) AssetDatabase.CreateFolder("Assets", "Scenes");
            return "Assets/Scenes";
        }

        public static void EnsureAssetFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = System.IO.Path.GetDirectoryName(path)?.Replace('\\', '/');
            string name = System.IO.Path.GetFileName(path);
            if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent)) EnsureAssetFolder(parent);
            if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(parent)) AssetDatabase.CreateFolder(parent, name);
        }

        public static void SaveTexture(Texture2D tex, string path)
        {
            var bytes = tex.EncodeToPNG();
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            File.WriteAllBytes(path, bytes);
        }
    }
}
