// 场景装配：把「剧情 SceneSpec + 灰盒 GrayboxPlan + 搭建 BuildResult」变成可游玩的世界。
// 这里是四层 Agent 流水线的落地端——所有资产都通过自研 GLB 加载器进入引擎。
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using EpicSpark.Core;
using EpicSpark.Build;
using EpicSpark.Npc.Runtime;
using EpicSpark.Anchors;

namespace EpicSpark.Loading
{
    /// <summary>一个已装配好的场景实例（支持场景复用：保留在内存中随时重新激活）。</summary>
    public class SceneInstance
    {
        public string sceneId = "";
        public GameObject root;
        public GameObject groundRoot;
        public GameObject grayboxRoot;
        public GameObject wireRoot;
        public GameObject modelRoot;
        public List<ModelView> models = new List<ModelView>();
        public List<GameObject> anchorObjects = new List<GameObject>();
        public List<GameObject> npcObjects = new List<GameObject>();
        public Vector3 spawnPoint = new Vector3(0f, 1.2f, 0f);
        public float width = 60f;
        public float length = 60f;

        /// <summary>场景里墙体 / 栅栏的平面包围盒（固定障碍，不参与推动）。</summary>
        public List<PlacementFootprint> wallFootprints = new List<PlacementFootprint>();
        /// <summary>按模型真实网格包围盒算出的平面包围盒（用于「不穿模」最终复核）。</summary>
        public List<PlacementFootprint> modelFootprints = new List<PlacementFootprint>();
        /// <summary>模型 + 墙体的全部空间障碍，供 NPC / 锚点找空地使用。</summary>
        public List<PlacementFootprint> obstacles = new List<PlacementFootprint>();
        /// <summary>已解析出的 NPC / 锚点世界坐标（按 bindId 缓存，场景复用时沿用）。</summary>
        public Dictionary<string, Vector3> actorPositions = new Dictionary<string, Vector3>(StringComparer.Ordinal);
        /// <summary>已生成的空气墙（不可见碰撞体）数量。</summary>
        public int airWallCount;
    }

    /// <summary>单个已放置模型的运行时视图。</summary>
    public class ModelView
    {
        public string slotId = "";
        public string modelId = "";
        public GameObject gameObject;
        public MeshRenderer renderer;
        public GlbModel model;
        public Material flatMaterial;
        public float distance;
        public bool revealed;
        /// <summary>模型网格的局部包围盒（未缩放），用于按真实尺寸计算空气墙与穿模约束。</summary>
        public Bounds localBounds;
        /// <summary>加载动画用的棱线对象；模型被约束求解挪动时需要一起平移。</summary>
        public GameObject wireObject;
    }

    public static class SceneAssembler
    {
        private static Material _groundMaterial;
        private static Material _flatMaterial;
        private static Material _cyanMaterial;

        public static Material FlatMaterial
        {
            get
            {
                if (_flatMaterial == null)
                {
                    Shader shader = Shader.Find("Universal Render Pipeline/Lit");
                    if (shader == null) shader = Shader.Find("Standard");
                    _flatMaterial = new Material(shader) { name = "Untextured" };
                    Color clay = new Color(0.82f, 0.82f, 0.80f, 1f);
                    if (_flatMaterial.HasProperty("_BaseColor")) _flatMaterial.SetColor("_BaseColor", clay);
                    if (_flatMaterial.HasProperty("_Color")) _flatMaterial.SetColor("_Color", clay);
                    if (_flatMaterial.HasProperty("_Smoothness")) _flatMaterial.SetFloat("_Smoothness", 0.08f);
                }
                return _flatMaterial;
            }
        }

        public static Material CyanMaterial
        {
            get
            {
                if (_cyanMaterial == null)
                {
                    Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
                    if (shader == null) shader = Shader.Find("Unlit/Color");
                    _cyanMaterial = new Material(shader) { name = "CyanEdge" };
                    Color cyan = new Color(0.18f, 0.95f, 0.85f, 1f);
                    if (_cyanMaterial.HasProperty("_BaseColor")) _cyanMaterial.SetColor("_BaseColor", cyan);
                    if (_cyanMaterial.HasProperty("_Color")) _cyanMaterial.SetColor("_Color", cyan);
                }
                return _cyanMaterial;
            }
        }

        private static Material GroundMaterial
        {
            get
            {
                if (_groundMaterial == null)
                {
                    Shader shader = Shader.Find("Universal Render Pipeline/Lit");
                    if (shader == null) shader = Shader.Find("Standard");
                    _groundMaterial = new Material(shader) { name = "Ground" };
                    Color soil = new Color(0.34f, 0.31f, 0.26f, 1f);
                    if (_groundMaterial.HasProperty("_BaseColor")) _groundMaterial.SetColor("_BaseColor", soil);
                    if (_groundMaterial.HasProperty("_Color")) _groundMaterial.SetColor("_Color", soil);
                    if (_groundMaterial.HasProperty("_Smoothness")) _groundMaterial.SetFloat("_Smoothness", 0.05f);
                }
                return _groundMaterial;
            }
        }

        public static async Task<SceneInstance> AssembleAsync(
            SceneSpec spec,
            GrayboxPlan plan,
            BuildResult build,
            Transform parent,
            CancellationToken ct)
        {
            var instance = new SceneInstance
            {
                sceneId = spec != null ? spec.sceneId : "",
                width = plan != null && plan.width > 1f ? plan.width : (spec != null ? spec.width : 60f),
                length = plan != null && plan.length > 1f ? plan.length : (spec != null ? spec.length : 60f),
            };

            instance.root = new GameObject("Scene_" + (string.IsNullOrEmpty(instance.sceneId) ? "unnamed" : instance.sceneId));
            if (parent != null) instance.root.transform.SetParent(parent, false);

            BuildTerrain(instance, plan);
            instance.grayboxRoot = GrayboxRenderer.Build(plan, instance.root.transform);
            instance.wireRoot = new GameObject("Wireframes");
            instance.wireRoot.transform.SetParent(instance.root.transform, false);
            instance.modelRoot = new GameObject("Models");
            instance.modelRoot.transform.SetParent(instance.root.transform, false);

            if (plan != null && plan.spawnPoint != Vector2.zero)
                instance.spawnPoint = new Vector3(plan.spawnPoint.x, 1.2f, plan.spawnPoint.y);
            else if (spec != null)
                instance.spawnPoint = new Vector3(spec.spawnPoint.x, Mathf.Max(1.2f, spec.spawnPoint.y), spec.spawnPoint.z);

            var placedBySlot = new Dictionary<string, PlacedAsset>(StringComparer.Ordinal);
            var unitByBindId = new Dictionary<string, PlacedAsset>(StringComparer.Ordinal);
            if (build != null && build.placed != null)
            {
                foreach (PlacedAsset placed in build.placed)
                {
                    if (placed == null) continue;
                    if (!string.IsNullOrEmpty(placed.slotId) && !placedBySlot.ContainsKey(placed.slotId))
                        placedBySlot[placed.slotId] = placed;
                    if (!string.IsNullOrEmpty(placed.bindId) && !unitByBindId.ContainsKey(placed.bindId))
                    {
                        if (placed.kind == SlotKind.Npc || placed.kind == SlotKind.Anchor)
                            unitByBindId[placed.bindId] = placed;
                    }
                }
            }

            if (build != null && build.placed != null)
            {
                foreach (PlacedAsset placed in build.placed)
                {
                    if (ct.IsCancellationRequested) break;
                    if (placed == null) continue;
                    if (placed.kind == SlotKind.Npc || placed.kind == SlotKind.Anchor) continue; // 由下方专门处理
                    ModelView view = await CreateModelAsync(placed, instance, ct);
                    if (view != null) instance.models.Add(view);
                }
            }

            // 用模型的真实网格包围盒做最终复核：彻底消除因「等比缩放」造成的尺寸误差重叠，
            // 并建立「模型 + 墙体」障碍集合，供 NPC / 锚点 / 出生点找空地。
            ResolveModelFootprints(instance, placedBySlot);

            CreateAnchors(spec, plan, instance, unitByBindId);
            CreateNpcs(spec, plan, instance, unitByBindId);
            ResolveSpawnPoint(instance);

            return instance;
        }

        // ─────────────────────────── 地形与区域 ───────────────────────────

        private static void BuildTerrain(SceneInstance instance, GrayboxPlan plan)
        {
            instance.groundRoot = new GameObject("Terrain");
            instance.groundRoot.transform.SetParent(instance.root.transform, false);

            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.SetParent(instance.groundRoot.transform, false);
            // Plane 原始尺寸为 10×10，缩放到场景尺寸
            ground.transform.localScale = new Vector3(instance.width / 10f, 1f, instance.length / 10f);
            ground.transform.localPosition = Vector3.zero;
            var renderer = ground.GetComponent<MeshRenderer>();
            if (renderer != null) renderer.sharedMaterial = GroundMaterial;

            BuildGroundPlan(plan, instance);
            BuildBoundaryAirWalls(instance);
        }

        // ─────────────────────────── 空气墙 ───────────────────────────

        /// <summary>模型空气墙的额外外扩量（米）：让玩家贴墙时相机不会穿进模型表面。</summary>
        private const float AirWallPadding = 0.05f;

        /// <summary>
        /// 给单个被摆放的模型挂一个不可见的 BoxCollider 空气墙（跟随模型缩放与旋转）。
        /// 模型本身只有 MeshRenderer，没有碰撞体，玩家会直接穿过；这里按网格局部包围盒补上。
        /// </summary>
        private static void AddModelAirWall(SceneInstance instance, GameObject modelGo, Bounds localBounds, float uniformScale)
        {
            if (modelGo == null) return;
            var wall = new GameObject("AirWall");
            wall.transform.SetParent(modelGo.transform, false);
            wall.transform.localPosition = Vector3.zero;
            wall.transform.localRotation = Quaternion.identity;
            wall.transform.localScale = Vector3.one;

            var box = wall.AddComponent<BoxCollider>();
            float pad = AirWallPadding / Mathf.Max(0.0001f, Mathf.Abs(uniformScale));
            Vector3 size = localBounds.size;
            size.x = Mathf.Max(0.02f, size.x + pad * 2f);
            size.y = Mathf.Max(0.02f, size.y + pad * 2f);
            size.z = Mathf.Max(0.02f, size.z + pad * 2f);
            box.center = localBounds.center;
            box.size = size;
            box.isTrigger = false;

            if (instance != null) instance.airWallCount++;
        }

        /// <summary>
        /// 场景边界空气墙：地面 Plane 是有限的，没有围墙时玩家会走出场景掉下去。
        /// 放在场景边缘内侧，不可见、不可穿透。
        /// </summary>
        private static void BuildBoundaryAirWalls(SceneInstance instance)
        {
            if (instance == null || instance.root == null) return;
            var root = new GameObject("BoundaryAirWalls");
            root.transform.SetParent(instance.root.transform, false);

            float halfW = Mathf.Max(4f, instance.width * 0.5f);
            float halfL = Mathf.Max(4f, instance.length * 0.5f);
            const float height = 8f;
            const float thickness = 0.4f;

            AddBoundaryWall(root.transform, "Boundary_West",
                new Vector3(-halfW + thickness * 0.5f, height * 0.5f, 0f),
                new Vector3(thickness, height, instance.length));
            AddBoundaryWall(root.transform, "Boundary_East",
                new Vector3(halfW - thickness * 0.5f, height * 0.5f, 0f),
                new Vector3(thickness, height, instance.length));
            AddBoundaryWall(root.transform, "Boundary_South",
                new Vector3(0f, height * 0.5f, -halfL + thickness * 0.5f),
                new Vector3(instance.width, height, thickness));
            AddBoundaryWall(root.transform, "Boundary_North",
                new Vector3(0f, height * 0.5f, halfL - thickness * 0.5f),
                new Vector3(instance.width, height, thickness));
        }

        private static void AddBoundaryWall(Transform parent, string name, Vector3 center, Vector3 size)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = center;
            var box = go.AddComponent<BoxCollider>();
            box.center = Vector3.zero;
            box.size = size;
            box.isTrigger = false;
        }

        // ─────────────────────────── 地编：地面分区与道路 ───────────────────────────

        /// <summary>
        /// 把灰盒规划里的「地面分区」与「道路」真正铺成几何体。
        /// 之前这里只画了加载动画用的灰线，动画结束就 SetActive(false)，
        /// 于是最终场景只剩一块空地 + 散落模型——看起来就是"乱拼"。
        /// </summary>
        private static void BuildGroundPlan(GrayboxPlan plan, SceneInstance instance)
        {
            if (plan == null) return;
            var root = new GameObject("GroundPlan");
            root.transform.SetParent(instance.groundRoot.transform, false);

            if (plan.regions != null)
            {
                for (int i = 0; i < plan.regions.Count; i++)
                {
                    GrayboxRegion region = plan.regions[i];
                    if (region == null || region.polygon == null || region.polygon.Count < 3) continue;
                    BuildRegionPatch(region, root.transform);
                }
            }

            if (plan.paths != null)
            {
                for (int i = 0; i < plan.paths.Count; i++)
                {
                    GrayboxPath path = plan.paths[i];
                    if (path == null || path.nodes == null || path.nodes.Count < 2) continue;
                    if (path.kind == "wall" || path.kind == "fence")
                        BuildWallSegments(path, root.transform, i, instance);
                    else
                        BuildRoadStrips(path, root.transform, i);
                }
            }
        }

        private static void BuildRegionPatch(GrayboxRegion region, Transform parent)
        {
            int count = region.polygon.Count;
            var vertices = new Vector3[count];
            float y = region.groundHeight + 0.02f;
            for (int i = 0; i < count; i++)
                vertices[i] = new Vector3(region.polygon[i].x, y, region.polygon[i].y);

            var triangles = new int[(count - 2) * 3];
            for (int i = 1; i < count - 1; i++)
            {
                int t = (i - 1) * 3;
                triangles[t] = 0;
                triangles[t + 1] = i;
                triangles[t + 2] = i + 1;
            }

            var mesh = new Mesh { name = "Region_" + region.kind };
            mesh.vertices = vertices;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            var go = new GameObject("Region_" + (string.IsNullOrEmpty(region.kind) ? "ground" : region.kind));
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = GroundKindMaterial(region.kind);
        }

        private static void BuildRoadStrips(GrayboxPath path, Transform parent, int index)
        {
            float half = Mathf.Max(1.2f, path.width) * 0.5f;
            var group = new GameObject("Road_" + index);
            group.transform.SetParent(parent, false);
            Material material = RoadMaterial;
            float y = 0.05f + (index % 3) * 0.002f;

            for (int i = 0; i < path.nodes.Count - 1; i++)
            {
                Vector2 a = path.nodes[i];
                Vector2 b = path.nodes[i + 1];
                Vector2 dir = b - a;
                if (dir.sqrMagnitude < 0.01f) continue;
                dir.Normalize();
                var normal = new Vector2(-dir.y, dir.x) * half;

                var verts = new[]
                {
                    new Vector3(a.x + normal.x, y, a.y + normal.y),
                    new Vector3(b.x + normal.x, y, b.y + normal.y),
                    new Vector3(b.x - normal.x, y, b.y - normal.y),
                    new Vector3(a.x - normal.x, y, a.y - normal.y),
                };
                var tris = new[] { 0, 1, 2, 0, 2, 3 };
                var mesh = new Mesh { name = "RoadStrip_" + index + "_" + i };
                mesh.vertices = verts;
                mesh.triangles = tris;
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();

                var strip = new GameObject("Strip_" + i);
                strip.transform.SetParent(group.transform, false);
                strip.AddComponent<MeshFilter>().sharedMesh = mesh;
                strip.AddComponent<MeshRenderer>().sharedMaterial = material;
            }
        }

        private static void BuildWallSegments(GrayboxPath path, Transform parent, int index, SceneInstance instance)
        {
            var group = new GameObject("Wall_" + index);
            group.transform.SetParent(parent, false);
            float height = Mathf.Clamp(path.width > 0f ? path.width : 2.5f, 1.5f, 6f);

            for (int i = 0; i < path.nodes.Count - 1; i++)
            {
                Vector2 a = path.nodes[i];
                Vector2 b = path.nodes[i + 1];
                Vector2 delta = b - a;
                float length = delta.magnitude;
                if (length < 0.5f) continue;

                var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
                wall.name = "WallSeg_" + i;
                wall.transform.SetParent(group.transform, false);
                wall.transform.localScale = new Vector3(length, height, 0.6f);
                Vector2 mid = (a + b) * 0.5f;
                wall.transform.position = new Vector3(mid.x, height * 0.5f, mid.y);
                wall.transform.rotation = Quaternion.Euler(0f, -Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg, 0f);
                var renderer = wall.GetComponent<MeshRenderer>();
                if (renderer != null) renderer.sharedMaterial = WallMaterial;

                // 墙体是固定障碍：模型 / NPC / 锚点 / 出生点都要避开它。
                if (instance != null)
                {
                    PlacementFootprint footprint = ScenePlacementSolver.CreateWallFootprint(
                        a, b, (path.pathId ?? ("wall_" + index)) + "_" + i);
                    if (footprint != null) instance.wallFootprints.Add(footprint);
                }
            }
        }

        private static readonly Dictionary<string, Material> KindMaterials = new Dictionary<string, Material>(StringComparer.Ordinal);

        private static Material GroundKindMaterial(string kind)
        {
            string key = string.IsNullOrEmpty(kind) ? "ground" : kind;
            Material cached;
            if (KindMaterials.TryGetValue(key, out cached) && cached != null) return cached;

            Color color;
            switch (key)
            {
                case "water": color = new Color(0.17f, 0.29f, 0.41f, 1f); break;
                case "field": color = new Color(0.28f, 0.35f, 0.20f, 1f); break;
                case "square": color = new Color(0.56f, 0.53f, 0.47f, 1f); break;
                case "market": color = new Color(0.52f, 0.44f, 0.34f, 1f); break;
                case "church": color = new Color(0.50f, 0.48f, 0.46f, 1f); break;
                case "military": color = new Color(0.38f, 0.36f, 0.30f, 1f); break;
                default: color = new Color(0.42f, 0.39f, 0.33f, 1f); break;
            }
            Material material = CreateLitMaterial("Ground_" + key, color, 0.06f);
            KindMaterials[key] = material;
            return material;
        }

        private static Material _roadMaterial;
        private static Material _wallMaterial;

        private static Material RoadMaterial
        {
            get
            {
                if (_roadMaterial == null) _roadMaterial = CreateLitMaterial("Road", new Color(0.40f, 0.38f, 0.35f, 1f), 0.12f);
                return _roadMaterial;
            }
        }

        private static Material WallMaterial
        {
            get
            {
                if (_wallMaterial == null) _wallMaterial = CreateLitMaterial("Wall", new Color(0.55f, 0.52f, 0.47f, 1f), 0.05f);
                return _wallMaterial;
            }
        }

        private static Material CreateLitMaterial(string name, Color color, float smoothness)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            var material = new Material(shader) { name = name };
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color")) material.SetColor("_Color", color);
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", smoothness);
            return material;
        }

        // ─────────────────────────── 模型 ───────────────────────────

        private static async Task<ModelView> CreateModelAsync(PlacedAsset placed, SceneInstance instance, CancellationToken ct)
        {
            string fileName = await ResolveGlbPathAsync(placed.modelId, ct);
            if (string.IsNullOrEmpty(fileName)) return null;

            GlbModel model = await GlbModelLoader.LoadAsync(fileName, ct);
            if (model == null || model.mesh == null)
            {
                Debug.LogWarning("[装配] 模型加载失败，改用占位方块: " + placed.modelId);
                return CreateFallbackBox(placed, instance);
            }

            var go = new GameObject(string.IsNullOrEmpty(placed.instanceName) ? placed.modelId : placed.instanceName);
            go.transform.SetParent(instance.modelRoot.transform, false);

            var filter = go.AddComponent<MeshFilter>();
            filter.sharedMesh = model.mesh;
            var meshRenderer = go.AddComponent<MeshRenderer>();
            meshRenderer.sharedMaterial = FlatMaterial; // 显现阶段再换成带贴图材质

            Bounds bounds = model.bounds;
            float baseWidth = Mathf.Max(0.0001f, bounds.size.x);
            float baseDepth = Mathf.Max(0.0001f, bounds.size.z);
            // 等比缩放：按占地的长宽分别拉伸会把建筑压扁（"像乱拼"的观感来源之一），
            // 这里取两轴缩放均值，保持资产原本比例。
            float scaleX = Mathf.Max(0.05f, placed.sizeX) / baseWidth;
            float scaleZ = Mathf.Max(0.05f, placed.sizeY) / baseDepth;
            float scaleY = Mathf.Clamp((scaleX + scaleZ) * 0.5f, 0.02f, 400f);
            go.transform.localScale = new Vector3(scaleY, scaleY, scaleY);

            Vector3 worldPos = GrayboxRenderer.ToWorld(new Vector2(placed.position.x, placed.position.z), 0f);
            float bottomOffset = -bounds.min.y * scaleY;
            go.transform.position = new Vector3(worldPos.x, bottomOffset, worldPos.z);
            go.transform.rotation = Quaternion.Euler(0f, placed.rotationY, 0f);

            // 空气墙：模型只有 MeshRenderer，玩家原本会直接穿过去；按网格局部包围盒补一个不可见碰撞体。
            AddModelAirWall(instance, go, bounds, scaleY);

            GameObject wire = CreateWireframe(go, model, instance);

            return new ModelView
            {
                slotId = placed.slotId,
                modelId = placed.modelId,
                gameObject = go,
                renderer = meshRenderer,
                model = model,
                flatMaterial = FlatMaterial,
                distance = Vector3.Distance(instance.spawnPoint, go.transform.position),
                localBounds = bounds,
                wireObject = wire,
            };
        }

        private static ModelView CreateFallbackBox(PlacedAsset placed, SceneInstance instance)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "fallback_" + placed.modelId;
            go.transform.SetParent(instance.modelRoot.transform, false);
            go.transform.localScale = new Vector3(placed.sizeX, Mathf.Max(1f, placed.sizeY * 0.5f), placed.sizeY);
            go.transform.position = new Vector3(placed.position.x, go.transform.localScale.y * 0.5f, placed.position.z);
            go.transform.rotation = Quaternion.Euler(0f, placed.rotationY, 0f);
            var renderer = go.GetComponent<MeshRenderer>();
            if (renderer != null) renderer.sharedMaterial = FlatMaterial;

            // 占位方块自带 BoxCollider，正好作为空气墙；这里额外记录数量，便于审计。
            Collider fallbackCollider = go.GetComponent<Collider>();
            if (fallbackCollider != null)
            {
                fallbackCollider.isTrigger = false;
                if (instance != null) instance.airWallCount++;
            }

            return new ModelView
            {
                slotId = placed.slotId,
                modelId = placed.modelId,
                gameObject = go,
                renderer = renderer,
                model = null,
                flatMaterial = FlatMaterial,
                distance = Vector3.Distance(instance.spawnPoint, go.transform.position),
                localBounds = new Bounds(Vector3.zero, Vector3.one),
                wireObject = null,
            };
        }

        /// <summary>为模型生成蓝色线的「棱」，作为显现阶段的矢量图。</summary>
        private static GameObject CreateWireframe(GameObject target, GlbModel model, SceneInstance instance)
        {
            if (model.creasedEdges == null || model.creasedEdges.Length < 2) return null;
            var wire = new GameObject("wire");
            wire.transform.SetParent(target.transform, false);
            wire.transform.localPosition = Vector3.zero;
            wire.transform.localScale = Vector3.one;

            var mesh = new Mesh { name = "wire" };
            mesh.vertices = model.mesh.vertices;
            mesh.SetIndices(model.creasedEdges, MeshTopology.Lines, 0);
            mesh.RecalculateBounds();

            var filter = wire.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;
            var renderer = wire.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = CyanMaterial;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            wire.transform.SetParent(instance.wireRoot.transform, true);
            // 保持与父模型相同的世界变换
            wire.transform.position = target.transform.position;
            wire.transform.rotation = target.transform.rotation;
            wire.transform.localScale = target.transform.localScale;
            target.transform.SetParent(instance.modelRoot.transform, true);
            return wire;
        }

        // ─────────────────────────── 模型真实包围盒复核 ───────────────────────────

        /// <summary>
        /// 模型加载完成后，用网格真实包围盒（而不是搭建阶段请求的尺寸）复核一遍「不允许穿模」：
        /// 等比缩放会让实际占地与请求尺寸不相等，这里以真实几何为准再分离一次；
        /// 同时把「模型 + 墙体」写入实例障碍集合，供 NPC / 锚点 / 出生点避让。
        /// </summary>
        private static void ResolveModelFootprints(SceneInstance instance, Dictionary<string, PlacedAsset> placedBySlot)
        {
            if (instance == null) return;

            var footprints = new List<PlacementFootprint>(instance.models.Count);
            var originals = new List<Vector2>(instance.models.Count);
            for (int i = 0; i < instance.models.Count; i++)
            {
                PlacementFootprint footprint = BuildWorldFootprint(instance.models[i], placedBySlot);
                if (footprint == null) continue;
                footprints.Add(footprint);
                originals.Add(footprint.center);
            }

            var report = new PlacementReport();
            ScenePlacementSolver.ResolveModelOverlaps(
                footprints, instance.wallFootprints, instance.width * 0.5f, instance.length * 0.5f, report);

            // 把求解结果同步到 GameObject、加载动画的棱线与 PlacedAsset（用于调试落盘）。
            for (int i = 0; i < footprints.Count; i++)
            {
                PlacementFootprint footprint = footprints[i];
                Vector2 delta = footprint.center - originals[i];
                if (delta.sqrMagnitude < 0.000001f) continue;

                ModelView view = instance.models[i];
                if (view == null || view.gameObject == null) continue;
                view.gameObject.transform.position += new Vector3(delta.x, 0f, delta.y);
                if (view.wireObject != null)
                    view.wireObject.transform.position += new Vector3(delta.x, 0f, delta.y);

                PlacedAsset asset = null;
                if (placedBySlot != null) placedBySlot.TryGetValue(view.slotId, out asset);
                if (asset != null)
                {
                    asset.position += new Vector3(delta.x, 0f, delta.y);
                    asset.sizeX = Mathf.Max(0.1f, footprint.halfX * 2f);
                    asset.sizeY = Mathf.Max(0.1f, footprint.halfY * 2f);
                }
            }

            instance.modelFootprints.AddRange(footprints);
            for (int i = 0; i < instance.wallFootprints.Count; i++) instance.obstacles.Add(instance.wallFootprints[i]);
            for (int i = 0; i < footprints.Count; i++) instance.obstacles.Add(footprints[i]);

            Debug.Log("[装配] 空气墙 " + instance.airWallCount + " 个；模型真实包围盒复核："
                + instance.models.Count + " 个模型 / " + instance.wallFootprints.Count + " 段墙体，"
                + "约束重定位 " + report.resolvedOverlapPairs + " 对，残留重叠 " + report.unresolvedOverlaps + " 对。");
            if (report.unresolvedOverlaps > 0)
                Debug.LogWarning("[装配] 仍有 " + report.unresolvedOverlaps + " 对模型包围盒重叠！");
            for (int i = 0; i < report.warnings.Count; i++) Debug.LogWarning("[装配] " + report.warnings[i]);
        }

        /// <summary>按模型当前的 world transform 与网格局部包围盒，构造真实平面 OBB。</summary>
        private static PlacementFootprint BuildWorldFootprint(ModelView view, Dictionary<string, PlacedAsset> placedBySlot)
        {
            if (view == null || view.gameObject == null) return null;
            Transform transform = view.gameObject.transform;
            Bounds local = view.localBounds;
            Vector3 worldCenter = transform.TransformPoint(local.center);
            Vector3 lossy = transform.lossyScale;

            float mobility = 1f;
            PlacedAsset asset = null;
            if (placedBySlot != null) placedBySlot.TryGetValue(view.slotId, out asset);
            if (asset != null) mobility = ScenePlacementSolver.MobilityFor(asset);

            return new PlacementFootprint
            {
                id = view.slotId,
                center = new Vector2(worldCenter.x, worldCenter.z),
                halfX = Mathf.Max(0.05f, Mathf.Abs(local.size.x * lossy.x) * 0.5f),
                halfY = Mathf.Max(0.05f, Mathf.Abs(local.size.z * lossy.z) * 0.5f),
                rotationY = transform.eulerAngles.y,
                movable = true,
                mobility = mobility,
            };
        }

        /// <summary>出生点最后复核：绝不允许出生在模型或墙体内部。</summary>
        private static void ResolveSpawnPoint(SceneInstance instance)
        {
            if (instance == null) return;
            float halfW = instance.width * 0.5f;
            float halfL = instance.length * 0.5f;
            Vector2 spawn = new Vector2(instance.spawnPoint.x, instance.spawnPoint.z);
            if (ScenePlacementSolver.IsPointFree(spawn, ScenePlacementSolver.PlayerClearance,
                    instance.obstacles, halfW, halfL))
                return;

            Vector2 free;
            if (ScenePlacementSolver.TryFindFreePoint(spawn, ScenePlacementSolver.PlayerClearance,
                    instance.obstacles, halfW, halfL, out free))
            {
                instance.spawnPoint = new Vector3(free.x, instance.spawnPoint.y, free.y);
                Debug.Log("[装配] 出生点原本在模型/墙体内，已挪到空地 (" + free.x.ToString("0.#") + ", " + free.y.ToString("0.#") + ")。");
            }
            else
            {
                Debug.LogWarning("[装配] 出生点被模型/墙体占据且找不到空地，玩家可能卡住。");
            }
        }

        // ─────────────────────────── 锚点与 NPC ───────────────────────────

        private static void CreateAnchors(
            SceneSpec spec,
            GrayboxPlan plan,
            SceneInstance instance,
            Dictionary<string, PlacedAsset> unitByBindId)
        {
            if (spec == null || spec.anchors == null) return;
            foreach (SceneAnchorSpec anchor in spec.anchors)
            {
                if (anchor == null) continue;
                Vector3 position = ResolveActorPosition(anchor.anchorId, anchor.localPosition, SlotKind.Anchor, plan, instance, unitByBindId);
                var go = new GameObject("Anchor_" + anchor.anchorId);
                go.transform.SetParent(instance.root.transform, false);
                go.transform.position = position;
                var point = go.AddComponent<AnchorPoint>();
                point.Initialize(anchor);
                instance.anchorObjects.Add(go);
            }
        }

        private static void CreateNpcs(
            SceneSpec spec,
            GrayboxPlan plan,
            SceneInstance instance,
            Dictionary<string, PlacedAsset> unitByBindId)
        {
            if (spec == null || spec.npcs == null) return;
            foreach (NpcSpec npc in spec.npcs)
            {
                if (npc == null) continue;
                Vector3 position = ResolveActorPosition(npc.npcId, npc.localPosition, SlotKind.Npc, plan, instance, unitByBindId);

                var go = new GameObject("NPC_" + npc.npcId);
                go.transform.SetParent(instance.root.transform, false);
                go.transform.position = position;

                // 占位身体：没有人物美术资产，用胶囊体保证 NPC 可见、可被准星射线命中。
                var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                body.name = "Body";
                body.transform.SetParent(go.transform, false);
                body.transform.localPosition = new Vector3(0f, 0.9f, 0f);
                body.transform.localScale = new Vector3(0.55f, 0.9f, 0.55f);
                var renderer = body.GetComponent<MeshRenderer>();
                if (renderer != null) renderer.sharedMaterial = CreateNpcMaterial(npc);
                // 身体碰撞体改成 Trigger：只用于射线交互，不把玩家顶开；移动碰撞交给 CharacterController。
                Collider bodyCollider = body.GetComponent<Collider>();
                if (bodyCollider != null) bodyCollider.isTrigger = true;

                // 角色控制器：让 NPC 也被模型空气墙 / 场景墙体挡住，不会走进模型里。
                var controller = go.AddComponent<CharacterController>();
                controller.height = 1.8f;
                controller.radius = 0.35f;
                controller.center = new Vector3(0f, 0.9f, 0f);
                controller.skinWidth = 0.04f;
                controller.stepOffset = 0.3f;
                controller.slopeLimit = 45f;

                var avatar = go.AddComponent<NpcAvatar>();
                List<Vector2> route = null;
                bool loop = true;
                float speed = 1.2f;
                if (plan != null && plan.npcPaths != null)
                {
                    foreach (NpcPath path in plan.npcPaths)
                    {
                        if (path != null && path.npcId == npc.npcId)
                        {
                            route = SanitizeRoute(path.waypoints, instance);
                            loop = path.loop;
                            speed = path.speed;
                            break;
                        }
                    }
                }
                avatar.Initialize(npc, route, loop, speed);
                instance.npcObjects.Add(go);
            }
        }

        /// <summary>巡逻路点若落在模型 / 墙体内部，就换成最近的空地，避免 NPC 一直往建筑里挤。</summary>
        private static List<Vector2> SanitizeRoute(List<Vector2> waypoints, SceneInstance instance)
        {
            if (waypoints == null || waypoints.Count == 0) return null;
            float halfW = instance.width * 0.5f;
            float halfL = instance.length * 0.5f;
            var result = new List<Vector2>(waypoints.Count);
            for (int i = 0; i < waypoints.Count; i++)
            {
                Vector2 waypoint = waypoints[i];
                if (!ScenePlacementSolver.IsPointFree(waypoint, ScenePlacementSolver.NpcClearance,
                        instance.obstacles, halfW, halfL))
                {
                    Vector2 free;
                    if (ScenePlacementSolver.TryFindFreePoint(waypoint, ScenePlacementSolver.NpcClearance,
                            instance.obstacles, halfW, halfL, out free))
                        waypoint = free;
                }
                result.Add(waypoint);
            }
            return result;
        }

        /// <summary>
        /// 决定 NPC / 锚点的最终落点，优先级：
        ///   1) 搭建阶段已解算好的 PlacedAsset 位置；
        ///   2) 场景复用时缓存的位置（同一个 bindId）；
        ///   3) 剧情给的世界坐标；
        ///   4) 灰盒规划里同 bindId 的槽位；
        ///   5) 场景内默认安全位置。
        /// 最后再用「模型 + 墙体」障碍做一次净空检查，必要时挪到最近空地。
        /// </summary>
        private static Vector3 ResolveActorPosition(
            string bindId,
            Vector3 storyLocalPosition,
            SlotKind kind,
            GrayboxPlan plan,
            SceneInstance instance,
            Dictionary<string, PlacedAsset> unitByBindId)
        {
            float halfW = instance != null ? instance.width * 0.5f : 30f;
            float halfL = instance != null ? instance.length * 0.5f : 30f;
            float clearance = kind == SlotKind.Anchor ? ScenePlacementSolver.AnchorClearance : ScenePlacementSolver.NpcClearance;
            string id = bindId ?? "";

            Vector2 flat;
            bool resolved = false;

            PlacedAsset placed = null;
            if (unitByBindId != null && !string.IsNullOrEmpty(id)) unitByBindId.TryGetValue(id, out placed);
            if (placed != null)
            {
                flat = new Vector2(placed.position.x, placed.position.z);
                resolved = true;
            }
            else if (instance != null && instance.actorPositions.TryGetValue(id, out Vector3 cached))
            {
                flat = new Vector2(cached.x, cached.z);
                resolved = true;
            }
            else if (storyLocalPosition != Vector3.zero)
            {
                flat = new Vector2(storyLocalPosition.x, storyLocalPosition.z);
                resolved = true;
            }
            else
            {
                flat = Vector2.zero;
            }

            if (!resolved)
            {
                Vector2 slotPosition;
                if (TryFindPlanUnitPosition(plan, id, kind, out slotPosition))
                {
                    flat = slotPosition;
                    resolved = true;
                }
            }

            if (!resolved)
            {
                // 兜底：锚点放在场景内圈、NPC 放在出生点附近，再交给净空搜索。
                if (kind == SlotKind.Anchor)
                    flat = new Vector2(halfW * 0.55f, halfL * 0.55f);
                else if (instance != null)
                    flat = new Vector2(instance.spawnPoint.x + 2f, instance.spawnPoint.z + 2f);
                else
                    flat = Vector2.zero;
            }

            if (instance != null)
            {
                if (!ScenePlacementSolver.IsPointFree(flat, clearance, instance.obstacles, halfW, halfL))
                {
                    Vector2 free;
                    if (ScenePlacementSolver.TryFindFreePoint(flat, clearance, instance.obstacles, halfW, halfL, out free))
                    {
                        Debug.Log("[装配] " + (kind == SlotKind.Anchor ? "锚点 " : "NPC ") + id + " 原落点在模型/墙体内，已挪到空地。");
                        flat = free;
                    }
                    else
                    {
                        Debug.LogWarning("[装配] " + (kind == SlotKind.Anchor ? "锚点 " : "NPC ") + id + " 找不到模型外的空地。");
                    }
                }
            }

            float y = kind == SlotKind.Anchor ? 0.05f : 0f;
            Vector3 position = new Vector3(flat.x, y, flat.y);
            if (instance != null && !string.IsNullOrEmpty(id)) instance.actorPositions[id] = position;
            if (placed != null) placed.position = position;
            return position;
        }

        private static bool TryFindPlanUnitPosition(GrayboxPlan plan, string bindId, SlotKind kind, out Vector2 position)
        {
            position = Vector2.zero;
            if (plan == null || plan.slots == null || string.IsNullOrEmpty(bindId)) return false;
            for (int i = 0; i < plan.slots.Count; i++)
            {
                GrayboxSlot slot = plan.slots[i];
                if (slot == null || slot.kind != kind) continue;
                if (!string.Equals(slot.bindId, bindId, StringComparison.Ordinal)) continue;
                position = slot.position;
                return true;
            }
            return false;
        }

        private static Material CreateNpcMaterial(NpcSpec npc)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            var material = new Material(shader) { name = "NpcBody" };
            Color color;
            switch (npc.tag)
            {
                case "soldier": color = new Color(0.55f, 0.15f, 0.15f); break;
                case "noble": color = new Color(0.65f, 0.55f, 0.20f); break;
                case "merchant": color = new Color(0.20f, 0.45f, 0.30f); break;
                case "clergy": color = new Color(0.35f, 0.30f, 0.55f); break;
                default: color = new Color(0.45f, 0.42f, 0.38f); break;
            }
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color")) material.SetColor("_Color", color);
            return material;
        }

        // ─────────────────────────── 贴图显现 ───────────────────────────

        /// <summary>
        /// 贴图以「张」为单位、由近及远逐张一次性完整显现，整个过程约 duration 秒。
        /// 设计文档第六节第 4 步。
        /// </summary>
        public static async Task RevealTextureAsync(SceneInstance instance, Vector3 from, float duration, CancellationToken ct)
        {
            if (instance == null || instance.models.Count == 0) return;

            var ordered = new List<ModelView>(instance.models);
            foreach (ModelView view in ordered)
            {
                if (view != null && view.gameObject != null)
                    view.distance = Vector3.Distance(from, view.gameObject.transform.position);
            }
            ordered.Sort((a, b) => a.distance.CompareTo(b.distance));

            float maxDistance = 1f;
            for (int i = 0; i < ordered.Count; i++)
                if (ordered[i].distance > maxDistance) maxDistance = ordered[i].distance;

            float startTime = Time.time;
            int next = 0;
            while (next < ordered.Count)
            {
                if (ct.IsCancellationRequested) return;
                float elapsed = Time.time - startTime;
                float progress = duration <= 0f ? 1f : Mathf.Clamp01(elapsed / duration);
                float distanceGate = progress * maxDistance * 1.05f;

                while (next < ordered.Count && ordered[next].distance <= distanceGate)
                {
                    ApplyTexture(ordered[next]);
                    next++;
                }
                await Task.Yield();
            }
        }

        private static void ApplyTexture(ModelView view)
        {
            if (view == null || view.revealed) return;
            view.revealed = true;
            if (view.renderer != null && view.model != null && view.model.material != null)
                view.renderer.sharedMaterial = view.model.material;
        }

        /// <summary>模型 id 到 glb 文件名的约定：mdl_xxx_001 → mdl_xxx_001.glb。</summary>
        private static string ResolveGlbFile(string modelId)
        {
            if (string.IsNullOrEmpty(modelId)) return null;
            if (modelId.EndsWith(".glb", StringComparison.OrdinalIgnoreCase)) return modelId;
            return modelId + ".glb";
        }

        /// <summary>
        /// 模型 id → 真实 glb 路径。运行时生成的资产 id 是 gen_*，但文件沿用的是被克隆条目的 glb，
        /// 远端生成的条目则可能是绝对路径，因此必须查模型库，不能简单拼 id + ".glb"。
        /// </summary>
        private static async Task<string> ResolveGlbPathAsync(string modelId, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(modelId)) return null;

            IModelCatalog catalog = Services.Catalog;
            if (catalog != null)
            {
                try
                {
                    IReadOnlyList<ModelEntry> all = await catalog.AllAsync();
                    if (all != null && (_glbById == null || _glbByIdCount != all.Count))
                    {
                        var map = new Dictionary<string, string>(all.Count, StringComparer.Ordinal);
                        for (int i = 0; i < all.Count; i++)
                        {
                            ModelEntry entry = all[i];
                            if (entry != null && !string.IsNullOrEmpty(entry.id)) map[entry.id] = entry.glb;
                        }
                        _glbById = map;
                        _glbByIdCount = all.Count;
                    }
                    string resolved;
                    if (_glbById != null && _glbById.TryGetValue(modelId, out resolved)
                        && !string.IsNullOrEmpty(resolved))
                    {
                        return resolved;
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[装配] 查询模型库失败: " + e.Message);
                }
            }

            return ResolveGlbFile(modelId);
        }

        private static Dictionary<string, string> _glbById;
        private static int _glbByIdCount = -1;

        public static void DestroyInstance(SceneInstance instance)
        {
            if (instance == null || instance.root == null) return;
            UnityEngine.Object.Destroy(instance.root);
            instance.root = null;
        }

        /// <summary>
        /// 场景复用时的动态刷新：场景几何是现成的，但新的游戏流程会带来新的锚点与 NPC，
        /// 因此只重建这两类对象，不重走灰盒与搭建。
        /// </summary>
        public static void RefreshActors(SceneInstance instance, SceneSpec spec, GrayboxPlan plan)
        {
            if (instance == null || instance.root == null) return;

            if (instance.anchorObjects != null)
            {
                for (int i = 0; i < instance.anchorObjects.Count; i++)
                    if (instance.anchorObjects[i] != null) UnityEngine.Object.Destroy(instance.anchorObjects[i]);
                instance.anchorObjects.Clear();
            }
            if (instance.npcObjects != null)
            {
                for (int i = 0; i < instance.npcObjects.Count; i++)
                    if (instance.npcObjects[i] != null) UnityEngine.Object.Destroy(instance.npcObjects[i]);
                instance.npcObjects.Clear();
            }

            // 复用场景没有 BuildResult：位置从 actorPositions 缓存 + 灰盒槽位解析，
            // 仍然会按已保存的「模型 + 墙体」障碍做净空检查，保证新 NPC / 锚点不落进模型。
            var emptyBindings = new Dictionary<string, PlacedAsset>(StringComparer.Ordinal);
            CreateAnchors(spec, plan, instance, emptyBindings);
            CreateNpcs(spec, plan, instance, emptyBindings);
        }
    }
}
