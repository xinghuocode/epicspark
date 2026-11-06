using UnityEditor;
using UnityEngine;

namespace SparkCode.Generation
{
    public static class ProbeFbx
    {
        public static void Run()
        {
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(BlenderTempleImporter.ModelPath);
            if (asset == null) { Debug.LogError("PROBE: missing fbx"); return; }
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(asset);
            Debug.Log("PROBE root rot=" + inst.transform.rotation.eulerAngles + " scale=" + inst.transform.lossyScale);
            foreach (var t in inst.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == "Floor" || t.name == "Dome" || t.name == "Pool_Water" ||
                    t.name == "Island_Top" || t.name == "Ceiling" || t.name == "Walls" ||
                    t.name.Contains("InnerColumn_00") || t.name.Contains("OuterColumn_00"))
                {
                    var r = t.GetComponent<Renderer>();
                    Debug.Log("PROBE " + t.name + " pos=" + t.position.ToString("F2") +
                              " rot=" + t.rotation.eulerAngles.ToString("F1") +
                              (r != null ? " boundsCenter=" + r.bounds.center.ToString("F2") + " size=" + r.bounds.size.ToString("F2") +
                               " mat=" + (r.sharedMaterial != null ? r.sharedMaterial.name : "null") : ""));
                }
            }
            var counts = new System.Collections.Generic.Dictionary<string, int>();
            foreach (var r in inst.GetComponentsInChildren<MeshRenderer>(true))
            {
                string n = r.sharedMaterial != null ? r.sharedMaterial.name : "null";
                counts[n] = counts.TryGetValue(n, out int c) ? c + 1 : 1;
            }
            foreach (var kv in counts) Debug.Log("PROBE material " + kv.Key + " x" + kv.Value);
            Object.DestroyImmediate(inst);
        }
    }
}
