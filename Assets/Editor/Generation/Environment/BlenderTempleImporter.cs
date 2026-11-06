using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace SparkCode.Generation
{
    /// <summary>
    /// Imports the Blender-authored temple (Assets/Art/Models/Temple.fbx), assigns the project's
    /// URP materials to the imported material slots and returns the shared EnvironmentInfo.
    /// The temple is modelled in Tools/blender/build_temple.py and exported at real-world scale.
    /// </summary>
    public static class BlenderTempleImporter
    {
        public const string ModelPath = "Assets/Art/Models/Temple.fbx";

        public static EnvironmentInfo Build(TempleBuildKit kit)
        {
            EnsureImportSettings();

            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
            if (asset == null)
            {
                Debug.LogWarning("[BlenderTempleImporter] " + ModelPath + " not found, falling back to procedural temple.");
                return TempleEnvironmentBuilder.Build(kit);
            }

            var instance = PrefabUtility.InstantiatePrefab(asset) as GameObject;
            if (instance == null) instance = Object.Instantiate(asset);
            instance.name = "Temple_Blender";
            instance.transform.SetParent(kit.Root, false);
            // Keep the FBX importer's root rotation: Blender Z-up geometry is corrected by a -90deg X
            // rotation on the imported root. Resetting it would lay the whole temple on its side.
            instance.transform.localPosition = Vector3.zero;

            int renderers = 0;
            var bounds = new Bounds(Vector3.zero, Vector3.zero);
            bool hasBounds = false;
            foreach (var r in instance.GetComponentsInChildren<MeshRenderer>(true))
            {
                renderers++;
                AssignMaterial(kit, r);
                if (!hasBounds)
                {
                    bounds = r.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(r.bounds);
                }
            }
            Debug.Log("[BlenderTempleImporter] imported " + renderers + " renderers, bounds=" + bounds.size +
                      " center=" + bounds.center);

            var sparkAnchor = kit.Create("SparkAnchor", instance.transform);
            sparkAnchor.transform.position = TempleLayout.SparkAnchor;

            var cameraTarget = kit.Create("CameraTarget", instance.transform);
            cameraTarget.transform.position = TempleLayout.CameraTarget;

            return new EnvironmentInfo
            {
                Root = instance.transform,
                SparkAnchor = sparkAnchor.transform,
                CameraTarget = TempleLayout.CameraTarget
            };
        }

        static void EnsureImportSettings()
        {
            var importer = AssetImporter.GetAtPath(ModelPath) as ModelImporter;
            if (importer == null) return;

            bool dirty = false;
            if (Mathf.Abs(importer.globalScale - 1f) > 0.0001f) { importer.globalScale = 1f; dirty = true; }
            if (importer.materialImportMode != ModelImporterMaterialImportMode.ImportStandard)
            {
                importer.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
                dirty = true;
            }
            if (importer.importAnimation) { importer.importAnimation = false; dirty = true; }
            if (importer.importTangents != ModelImporterTangents.CalculateMikk)
            {
                importer.importTangents = ModelImporterTangents.CalculateMikk;
                dirty = true;
            }
            if (importer.importNormals != ModelImporterNormals.Import)
            {
                importer.importNormals = ModelImporterNormals.Import;
                dirty = true;
            }
            if (importer.meshCompression != ModelImporterMeshCompression.Off)
            {
                importer.meshCompression = ModelImporterMeshCompression.Off;
                dirty = true;
            }
            if (dirty) importer.SaveAndReimport();
        }

        static void AssignMaterial(TempleBuildKit kit, MeshRenderer r)
        {
            Material imported = r.sharedMaterial;
            string key = imported != null ? imported.name.ToLowerInvariant() : string.Empty;

            Material target;
            if (key.Contains("water")) target = kit.Water;
            else if (key.Contains("floor")) target = kit.MarbleFloor;
            else if (key.Contains("trim")) target = kit.MarbleTrim;
            else if (key.Contains("dark")) target = kit.MarbleDark;
            else if (key.Contains("basin")) target = kit.MarbleBasin;
            else if (key.Contains("gold")) target = kit.Gold;
            else target = kit.Marble;

            var mats = r.sharedMaterials;
            if (mats == null || mats.Length == 0) mats = new Material[1];
            for (int i = 0; i < mats.Length; i++) mats[i] = target;
            r.sharedMaterials = mats;

            bool isWater = target == kit.Water;
            r.shadowCastingMode = isWater
                ? UnityEngine.Rendering.ShadowCastingMode.Off
                : UnityEngine.Rendering.ShadowCastingMode.On;
            r.receiveShadows = !isWater;
        }
    }
}
