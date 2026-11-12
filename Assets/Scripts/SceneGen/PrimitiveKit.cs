// PrimitiveKit：素模几何库。
//
// 这一阶段故意不用任何美术资产（GLB / 模型库 / 资产生成服务），只用程序化基本体：
//   长方体、圆柱、圆锥（金字塔）、球、胶囊、楔形屋顶、台阶堆叠。
// 目的：先把「密度 / 秩序 / 落位」的核心逻辑验证干净，再做资产替换。
//
// 约定：所有构件在世界 XZ 上轴对齐，尺寸直接来自 LayoutSpec 的世界 AABB；
// 朝向只由 rotationY（faceDir 推出）决定，绝不用 rotation 去交换 x/z 尺寸。
using System;
using System.Collections.Generic;
using EpicSpark.Core;
using UnityEngine;

namespace EpicSpark.SceneGen
{
    public static class PrimitiveKit
    {
        private static readonly Dictionary<string, Material> Materials = new Dictionary<string, Material>(StringComparer.Ordinal);
        private static Mesh _cone12;
        private static Mesh _cone4;

        // ─────────────────────────── 材质 ───────────────────────────

        private static Material ShaderMaterial(string name, Color color, float smoothness)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            var material = new Material(shader) { name = "Prim_" + name };
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color")) material.SetColor("_Color", color);
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", smoothness);
            if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", smoothness);
            return material;
        }

        public static Material Get(string key)
        {
            Material material;
            if (Materials.TryGetValue(key, out material) && material != null) return material;

            Color color;
            float smoothness = 0.08f;
            switch (key)
            {
                case "plaster": color = new Color(0.80f, 0.75f, 0.66f); break;
                case "plaster2": color = new Color(0.74f, 0.68f, 0.58f); break;
                case "plaster3": color = new Color(0.86f, 0.82f, 0.72f); break;
                case "roof": color = new Color(0.42f, 0.22f, 0.17f); break;
                case "roof_slate": color = new Color(0.26f, 0.28f, 0.32f); break;
                case "stone": color = new Color(0.72f, 0.70f, 0.66f); break;
                case "stone_dark": color = new Color(0.48f, 0.47f, 0.45f); break;
                case "wood": color = new Color(0.46f, 0.34f, 0.21f); break;
                case "cloth": color = new Color(0.78f, 0.70f, 0.52f); break;
                case "iron": color = new Color(0.20f, 0.20f, 0.23f); smoothness = 0.35f; break;
                case "lamp": color = new Color(1.00f, 0.88f, 0.55f); smoothness = 0.5f; break;
                case "leaf": color = new Color(0.27f, 0.42f, 0.21f); break;
                case "leaf2": color = new Color(0.35f, 0.48f, 0.24f); break;
                case "trunk": color = new Color(0.33f, 0.26f, 0.18f); break;
                case "crowd": color = new Color(0.55f, 0.42f, 0.36f); break;
                case "crowd2": color = new Color(0.40f, 0.42f, 0.50f); break;
                case "cyan": color = new Color(0.18f, 0.95f, 0.85f); smoothness = 0.5f; break;
                case "water": color = new Color(0.22f, 0.38f, 0.48f); smoothness = 0.6f; break;
                case "metal": color = new Color(0.35f, 0.35f, 0.38f); smoothness = 0.4f; break;
                default: color = new Color(0.62f, 0.60f, 0.56f); break;
            }
            material = ShaderMaterial(key, color, smoothness);
            Materials[key] = material;
            return material;
        }

        // ─────────────────────────── 基本体 ───────────────────────────

        public static GameObject Part(Transform parent, string name, PrimitiveType type, Vector3 localPos, Vector3 localScale,
            Material material, bool withCollider = true)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = localScale;
            var renderer = go.GetComponent<MeshRenderer>();
            if (renderer != null && material != null) renderer.sharedMaterial = material;
            if (!withCollider) StripCollider(go);
            return go;
        }

        public static GameObject Cone(Transform parent, string name, Vector3 localPos, Vector3 localScale, Material material,
            int segments = 12, bool withCollider = false)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = localScale;
            var filter = go.AddComponent<MeshFilter>();
            filter.sharedMesh = segments <= 4 ? ConeMesh4() : ConeMesh12();
            var renderer = go.AddComponent<MeshRenderer>();
            if (material != null) renderer.sharedMaterial = material;
            if (withCollider)
            {
                var collider = go.AddComponent<MeshCollider>();
                collider.sharedMesh = filter.sharedMesh;
            }
            return go;
        }

        private static void StripCollider(GameObject go)
        {
            var collider = go.GetComponent<Collider>();
            if (collider == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(collider);
            else UnityEngine.Object.DestroyImmediate(collider);
        }

        private static Mesh ConeMesh12()
        {
            if (_cone12 != null) return _cone12;
            _cone12 = BuildCone(12);
            return _cone12;
        }

        private static Mesh ConeMesh4()
        {
            if (_cone4 != null) return _cone4;
            _cone4 = BuildCone(4);
            return _cone4;
        }

        /// <summary>底面半径 0.5、顶点在 y=1 的正圆锥（归一化，靠 localScale 缩放）。</summary>
        private static Mesh BuildCone(int segments)
        {
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            var uvs = new List<Vector2>();

            vertices.Add(new Vector3(0f, 1f, 0f)); // apex 0
            uvs.Add(new Vector2(0.5f, 1f));
            for (int i = 0; i < segments; i++)
            {
                float angle = Mathf.PI * 2f * i / segments;
                vertices.Add(new Vector3(Mathf.Cos(angle) * 0.5f, 0f, Mathf.Sin(angle) * 0.5f));
                uvs.Add(new Vector2((float)i / segments, 0f));
            }
            int baseCenter = vertices.Count;
            vertices.Add(Vector3.zero);
            uvs.Add(new Vector2(0.5f, 0.5f));

            for (int i = 0; i < segments; i++)
            {
                int a = 1 + i;
                int b = 1 + (i + 1) % segments;
                triangles.Add(0); triangles.Add(b); triangles.Add(a);
                triangles.Add(baseCenter); triangles.Add(a); triangles.Add(b);
            }

            var mesh = new Mesh { name = "PrimCone_" + segments };
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        // ─────────────────────────── 复合体 ───────────────────────────

        /// <summary>按 PlacedAsset.primitive 生成一个复合 GameObject（本地坐标居中于占地中心）。</summary>
        public static GameObject Build(PlacedAsset placed, Transform parent)
        {
            if (placed == null || parent == null) return null;
            string primitive = string.IsNullOrEmpty(placed.primitive) ? "building" : placed.primitive;
            float sx = Mathf.Max(0.2f, placed.sizeX);
            float sz = Mathf.Max(0.2f, placed.sizeY);
            float h = placed.height > 0.05f ? placed.height : Mathf.Max(1f, Mathf.Min(sx, sz) * 0.6f);

            // 尺寸语义：sizeX/sizeY 是"世界 XZ 轴对齐包围盒"。本地建模时 +Z 当作正立面，
            // 如果整栋旋转了 90°，必须把本地 X/Z 尺寸对调，否则旋转后实际占地会变成 (sizeY,sizeX)，
            // 排屋之间就会出现"应贴齐却差一个进深"的错位。
            float yaw = placed.rotationY;
            float localX, localZ;
            ResolveLocalFootprint(sx, sz, yaw, out localX, out localZ);

            var root = new GameObject("Prim_" + primitive + "_" + placed.slotId);
            root.transform.SetParent(parent, false);
            root.transform.localPosition = new Vector3(placed.position.x, 0f, placed.position.z);
            root.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);

            switch (primitive)
            {
                case "fortress": BuildFortress(root.transform, localX, localZ, h); break;
                case "church": BuildChurch(root.transform, localX, localZ, h); break;
                case "gate": BuildGate(root.transform, localX, localZ, h); break;
                case "hall": BuildHall(root.transform, localX, localZ, h); break;
                case "steps": BuildSteps(root.transform, localX, localZ, h); break;
                case "stall": BuildStall(root.transform, localX, localZ, h); break;
                case "lamp": BuildLamp(root.transform, h); break;
                case "tree": BuildTree(root.transform, Mathf.Max(localX, localZ), h); break;
                case "drum": BuildDrum(root.transform, Mathf.Max(localX, localZ), h); break;
                case "crowd": BuildCrowd(root.transform, Mathf.Max(localX, localZ), h, placed.slotId); break;
                case "anchor": BuildAnchorMarker(root.transform, Mathf.Max(localX, localZ)); break;
                case "pillar": BuildPillar(root.transform, Mathf.Max(localX, localZ), h); break;
                case "greenhouse": BuildRowHouse(root.transform, localX, localZ, h, "plaster"); break;
                case "row_house": BuildRowHouse(root.transform, localX, localZ, h, ColorKey(placed.slotId, "plaster")); break;
                case "shop_house": BuildRowHouse(root.transform, localX, localZ, h, "plaster3"); break;
                case "row_arch": BuildRowHouse(root.transform, localX, localZ, h, "plaster2"); break;
                case "npc": BuildCrowd(root.transform, 0.6f, 1.75f, placed.slotId); break;
                case "grass":
                    Part(root.transform, "Grass", PrimitiveType.Cube, new Vector3(0f, 0.05f, 0f),
                        new Vector3(localX, 0.1f, localZ), Get("leaf"), false);
                    break;
                case "prop": BuildProp(root.transform, Mathf.Max(localX, localZ), h); break;
                default: BuildRowHouse(root.transform, localX, localZ, h, ColorKey(placed.slotId, "plaster")); break;
            }
            return root;
        }

        /// <summary>把世界 AABB 转成任意 yaw 下的本地占地；45° 的非方形无唯一解时采用保守缩小。</summary>
        private static void ResolveLocalFootprint(float sx, float sz, float yaw, out float localX, out float localZ)
        {
            Vector2 local = SceneGenOBB.LocalSizeFromWorldAABB(new Vector2(sx, sz), yaw);
            localX = local.x;
            localZ = local.y;
        }

        private static string ColorKey(string seed, string fallback)
        {
            if (string.IsNullOrEmpty(seed)) return fallback;
            unchecked
            {
                int hash = 0;
                for (int i = 0; i < seed.Length; i++) hash = hash * 31 + seed[i];
                int pick = Mathf.Abs(hash) % 3;
                if (pick == 1) return "plaster2";
                if (pick == 2) return "plaster3";
                return fallback;
            }
        }

        /// <summary>排屋/普通建筑：主体 + 四坡屋顶 + 烟囱。临街立面朝向由 root 旋转负责。</summary>
        private static void BuildRowHouse(Transform root, float sx, float sz, float h, string bodyKey)
        {
            Material body = Get(bodyKey);
            Material roof = Get("roof");
            float bodyH = h * 0.78f;
            Part(root, "Body", PrimitiveType.Cube, new Vector3(0f, bodyH * 0.5f, 0f),
                new Vector3(sx, bodyH, sz), body);
            float roofH = Mathf.Max(1.2f, h * 0.22f);
            Cone(root, "Roof", new Vector3(0f, bodyH, 0f),
                new Vector3(sx * 1.06f, roofH, sz * 1.06f), roof, 4);
            Part(root, "Chimney", PrimitiveType.Cube,
                new Vector3(sx * 0.28f, bodyH + roofH * 0.7f, sz * 0.24f),
                new Vector3(0.7f, 1.4f, 0.7f), Get("stone_dark"), false);
            // 临街底层门洞（颜色更深的一段，表示可进入的立面）；本地 +Z 是正立面。
            Part(root, "Shopfront", PrimitiveType.Cube,
                new Vector3(0f, bodyH * 0.28f, sz * 0.5f + 0.06f),
                new Vector3(sx * 0.5f, bodyH * 0.5f, 0.12f), Get("wood"), false);
        }

        private static void BuildFortress(Transform root, float sx, float sz, float h)
        {
            Material stone = Get("stone_dark");
            float bodyH = h * 0.72f;
            Part(root, "Keep", PrimitiveType.Cube, new Vector3(0f, bodyH * 0.5f, 0f), new Vector3(sx, bodyH, sz), stone);
            float r = Mathf.Min(sx, sz) * 0.11f;
            float th = h * 0.92f;
            float[] xs = { -sx * 0.5f + r, sx * 0.5f - r };
            float[] zs = { -sz * 0.5f + r, sz * 0.5f - r };
            for (int i = 0; i < 2; i++)
            for (int k = 0; k < 2; k++)
            {
                var center = new Vector3(xs[i], 0f, zs[k]);
                Part(root, "Tower", PrimitiveType.Cylinder, center + new Vector3(0f, th * 0.5f, 0f),
                    new Vector3(r * 2f, th * 0.5f, r * 2f), stone);
                Cone(root, "TowerRoof", center + new Vector3(0f, th, 0f),
                    new Vector3(r * 2.3f, h * 0.22f, r * 2.3f), Get("roof_slate"), 12);
            }
            // 雉堞：沿顶面一圈小块
            int count = Mathf.Clamp(Mathf.RoundToInt((sx + sz) / 4f), 6, 18);
            for (int i = 0; i < count; i++)
            {
                float t = (i + 0.5f) / count;
                float x = Mathf.Lerp(-sx * 0.45f, sx * 0.45f, t);
                Part(root, "Merlon", PrimitiveType.Cube,
                    new Vector3(x, bodyH + 0.6f, sz * 0.5f - 0.3f),
                    new Vector3(1.4f, 1.2f, 0.8f), stone, false);
            }
        }

        private static void BuildChurch(Transform root, float sx, float sz, float h)
        {
            Material stone = Get("stone");
            float bodyH = h * 0.62f;
            Part(root, "Nave", PrimitiveType.Cube, new Vector3(-sx * 0.12f, bodyH * 0.5f, 0f),
                new Vector3(sx * 0.72f, bodyH, sz * 0.66f), stone);
            Cone(root, "NaveRoof", new Vector3(-sx * 0.12f, bodyH, 0f),
                new Vector3(sx * 0.78f, h * 0.14f, sz * 0.72f), Get("roof_slate"), 4);
            float towerH = h * 0.88f;
            var towerPos = new Vector3(sx * 0.34f, 0f, -sz * 0.18f);
            Part(root, "Tower", PrimitiveType.Cylinder, towerPos + new Vector3(0f, towerH * 0.5f, 0f),
                new Vector3(sx * 0.2f, towerH * 0.5f, sx * 0.2f), stone);
            Cone(root, "Spire", towerPos + new Vector3(0f, towerH, 0f),
                new Vector3(sx * 0.22f, h * 0.34f, sx * 0.22f), Get("roof_slate"), 12);
        }

        private static void BuildGate(Transform root, float sx, float sz, float h)
        {
            Material stone = Get("stone");
            float towerW = Mathf.Max(2f, sx * 0.22f);
            float gap = Mathf.Max(2.5f, sx - towerW * 2f);
            for (int side = -1; side <= 1; side += 2)
            {
                Part(root, "GateTower", PrimitiveType.Cube,
                    new Vector3(side * (gap * 0.5f + towerW * 0.5f), h * 0.5f, 0f),
                    new Vector3(towerW, h, sz), stone);
            }
            float lintelY = h * 0.72f;
            Part(root, "Lintel", PrimitiveType.Cube,
                new Vector3(0f, lintelY + (h - lintelY) * 0.5f, 0f),
                new Vector3(gap, h - lintelY, sz * 0.8f), stone);
            Part(root, "Arch", PrimitiveType.Cube,
                new Vector3(0f, lintelY * 0.5f, sz * 0.42f), new Vector3(gap * 0.9f, lintelY, 0.15f),
                Get("iron"), false);
        }

        private static void BuildHall(Transform root, float sx, float sz, float h)
        {
            Material stone = Get("stone");
            float bodyH = h * 0.86f;
            Part(root, "Body", PrimitiveType.Cube, new Vector3(0f, bodyH * 0.5f, 0f), new Vector3(sx, bodyH, sz), stone);
            Part(root, "Cornice", PrimitiveType.Cube, new Vector3(0f, bodyH + 0.35f, 0f),
                new Vector3(sx * 1.04f, 0.7f, sz * 1.04f), Get("stone_dark"), false);
            // 正立面柱廊
            int columns = Mathf.Clamp(Mathf.RoundToInt(sx / 4f), 3, 9);
            for (int i = 0; i < columns; i++)
            {
                float t = columns == 1 ? 0.5f : (float)i / (columns - 1);
                float x = Mathf.Lerp(-sx * 0.44f, sx * 0.44f, t);
                Part(root, "Column", PrimitiveType.Cylinder,
                    new Vector3(x, bodyH * 0.45f, sz * 0.5f + 0.5f),
                    new Vector3(0.7f, bodyH * 0.45f, 0.7f), Get("stone_dark"), false);
            }
        }

        private static void BuildSteps(Transform root, float sx, float sz, float h)
        {
            Material stone = Get("stone_dark");
            int steps = 5;
            float totalH = Mathf.Max(0.6f, h);
            for (int i = 0; i < steps; i++)
            {
                float t = (i + 1f) / steps;
                float depth = sz * (1f - i * 0.16f);
                Part(root, "Step" + i, PrimitiveType.Cube,
                    new Vector3(0f, totalH * t * 0.5f, sz * 0.5f - depth * 0.5f),
                    new Vector3(sx * (1f - i * 0.05f), totalH * t, depth), stone, i == 0);
            }
        }

        private static void BuildStall(Transform root, float sx, float sz, float h)
        {
            Material wood = Get("wood");
            float postH = Mathf.Max(1.6f, h);
            for (int i = -1; i <= 1; i += 2)
            for (int k = -1; k <= 1; k += 2)
            {
                Part(root, "Post", PrimitiveType.Cube,
                    new Vector3(i * sx * 0.44f, postH * 0.5f, k * sz * 0.44f),
                    new Vector3(0.18f, postH, 0.18f), wood, false);
            }
            Part(root, "Canopy", PrimitiveType.Cube, new Vector3(0f, postH, 0f),
                new Vector3(sx * 1.08f, 0.16f, sz * 1.08f), Get("cloth"), false);
            Part(root, "Counter", PrimitiveType.Cube, new Vector3(0f, postH * 0.45f, 0f),
                new Vector3(sx * 0.9f, 0.5f, sz * 0.5f), wood, false);
        }

        private static void BuildLamp(Transform root, float h)
        {
            float postH = Mathf.Max(2.5f, h);
            Part(root, "Post", PrimitiveType.Cylinder, new Vector3(0f, postH * 0.5f, 0f),
                new Vector3(0.16f, postH * 0.5f, 0.16f), Get("iron"));
            Part(root, "Head", PrimitiveType.Cube, new Vector3(0f, postH + 0.3f, 0f),
                new Vector3(0.5f, 0.6f, 0.5f), Get("lamp"), false);
        }

        private static void BuildTree(Transform root, float sx, float h)
        {
            float trunkH = Mathf.Max(1.8f, h * 0.45f);
            Part(root, "Trunk", PrimitiveType.Cylinder, new Vector3(0f, trunkH * 0.5f, 0f),
                new Vector3(0.45f, trunkH * 0.5f, 0.45f), Get("trunk"));
            Part(root, "Crown", PrimitiveType.Sphere, new Vector3(0f, trunkH + h * 0.28f, 0f),
                new Vector3(Mathf.Max(1.6f, sx), h * 0.62f, Mathf.Max(1.6f, sx)), Get("leaf"), false);
        }

        private static void BuildDrum(Transform root, float sx, float h)
        {
            float drumH = Mathf.Max(0.8f, h);
            Part(root, "Drum", PrimitiveType.Cylinder, new Vector3(0f, drumH * 0.5f, 0f),
                new Vector3(sx, drumH * 0.5f, sx), Get("wood"));
            Part(root, "Skin", PrimitiveType.Cylinder, new Vector3(0f, drumH * 0.98f, 0f),
                new Vector3(sx * 1.02f, 0.08f, sx * 1.02f), Get("cloth"), false);
        }

        private static void BuildCrowd(Transform root, float sx, float h, string seed)
        {
            int count = 3;
            for (int i = 0; i < count; i++)
            {
                float angle = (i / (float)count) * Mathf.PI * 2f;
                float radius = Mathf.Max(0.6f, sx * 0.6f);
                Part(root, "Person" + i, PrimitiveType.Capsule,
                    new Vector3(Mathf.Cos(angle) * radius, h * 0.5f, Mathf.Sin(angle) * radius),
                    new Vector3(0.55f, h * 0.5f, 0.55f),
                    Get(ColorKey(seed + i, "crowd")), false);
            }
        }

        private static void BuildAnchorMarker(Transform root, float sx)
        {
            Part(root, "Ring", PrimitiveType.Cylinder, new Vector3(0f, 0.05f, 0f),
                new Vector3(Mathf.Max(1.6f, sx), 0.05f, Mathf.Max(1.6f, sx)), Get("cyan"), false);
            Part(root, "Beacon", PrimitiveType.Cube, new Vector3(0f, 1.6f, 0f),
                new Vector3(0.35f, 3.2f, 0.35f), Get("cyan"), false);
        }

        private static void BuildPillar(Transform root, float sx, float h)
        {
            Part(root, "Pillar", PrimitiveType.Cylinder, new Vector3(0f, h * 0.5f, 0f),
                new Vector3(sx, h * 0.5f, sx), Get("stone"));
        }

        private static void BuildProp(Transform root, float sx, float h)
        {
            Part(root, "Crate", PrimitiveType.Cube, new Vector3(0f, h * 0.5f, 0f),
                new Vector3(sx, h, sx), Get("wood"));
        }
    }
}
