// 场景装配：把「剧情 SceneSpec + 权威灰盒 GrayboxPlan + 素模 BuildResult」变成可游玩的世界。
// 当前只由 PrimitiveKit 创建几何体；历史 GLB 代码被 EPICSPARK_ENABLE_LEGACY_ASSETS 编译开关隔离。
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using EpicSpark.Core;
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
        /// <summary>生成该实例的权威灰盒状态；复用时按同一计划恢复锚点/NPC，禁止退回固定角落。</summary>
        public GrayboxPlan plan;
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
                plan = plan,
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

            EnsureSpecStructures(plan, build, instance);
            CreateAnchors(spec, plan, instance);
            CreateNpcs(spec, plan, instance);

            return instance;
        }

        /// <summary>
        /// 集市遮阳棚这类「有明确 10×22 占地、但美术库里没有合适模型」的规格槽位，
        /// 不能因为回退到 1.5 米木箱就从画面里消失。这里补一套灰盒棚架，
        /// 保证剧情布局里的结构至少可见；以后有真实资产且尺寸匹配时不重复生成。
        /// </summary>
        private static void EnsureSpecStructures(GrayboxPlan plan, BuildResult build, SceneInstance instance)
        {
            if (plan == null || plan.slots == null || instance == null) return;
            // 素模模式：每个槽位（含摊位/棚架）都会由 PrimitiveKit 生成几何体，不需要再补灰盒棚架。
            if (build != null && build.primitiveMode) return;
            for (int i = 0; i < plan.slots.Count; i++)
            {
                GrayboxSlot slot = plan.slots[i];
                if (slot == null || !slot.specRequired) continue;
                if (slot.kind != SlotKind.Prop && slot.kind != SlotKind.Decoration) continue;
                string semantic = slot.semantic ?? "";
                bool canopy = semantic.IndexOf("遮阳棚", StringComparison.Ordinal) >= 0
                              || semantic.IndexOf("木棚", StringComparison.Ordinal) >= 0
                              || (semantic.IndexOf("集市", StringComparison.Ordinal) >= 0
                                  && semantic.IndexOf("棚", StringComparison.Ordinal) >= 0);
                if (!canopy) continue;
                if (slot.sizeX < 5f && slot.sizeY < 5f) continue;

                bool alreadyCovered = false;
                if (build != null && build.placed != null)
                {
                    for (int p = 0; p < build.placed.Count; p++)
                    {
                        PlacedAsset placed = build.placed[p];
                        if (placed == null || placed.slotId != slot.slotId) continue;
                        if (placed.sizeX >= slot.sizeX * 0.55f && placed.sizeY >= slot.sizeY * 0.55f)
                            alreadyCovered = true;
                    }
                }
                if (!alreadyCovered) CreateCanopy(slot, instance);
            }
        }

        private static void CreateCanopy(GrayboxSlot slot, SceneInstance instance)
        {
            float width = Mathf.Max(3f, slot.sizeX);
            float depth = Mathf.Max(3f, slot.sizeY);
            float height = 2.8f;
            Vector3 center = GrayboxRenderer.ToWorld(slot.position, 0f);

            var root = new GameObject("SpecCanopy_" + slot.slotId);
            root.transform.SetParent(instance.modelRoot.transform, false);
            root.transform.position = new Vector3(center.x, 0f, center.z);
            root.transform.rotation = Quaternion.Euler(0f, slot.rotation, 0f);

            Material roofMat = CreateLitMaterial("CanopyRoof", new Color(0.52f, 0.34f, 0.20f, 1f), 0.02f);
            Material postMat = CreateLitMaterial("CanopyPost", new Color(0.24f, 0.16f, 0.10f, 1f), 0.02f);

            var roof = GameObject.CreatePrimitive(PrimitiveType.Cube);
            roof.name = "CanopyRoof";
            roof.transform.SetParent(root.transform, false);
            roof.transform.localScale = new Vector3(width, 0.18f, depth);
            roof.transform.localPosition = new Vector3(0f, height, 0f);
            var roofRenderer = roof.GetComponent<MeshRenderer>();
            if (roofRenderer != null) roofRenderer.sharedMaterial = roofMat;

            float px = Mathf.Max(0.6f, width * 0.42f);
            float pz = Mathf.Max(0.6f, depth * 0.42f);
            float[][] corners =
            {
                new[] { -px, -pz }, new[] { px, -pz },
                new[] { -px, pz }, new[] { px, pz },
            };
            for (int i = 0; i < corners.Length; i++)
            {
                var post = GameObject.CreatePrimitive(PrimitiveType.Cube);
                post.name = "CanopyPost_" + i;
                post.transform.SetParent(root.transform, false);
                post.transform.localScale = new Vector3(0.16f, height, 0.16f);
                post.transform.localPosition = new Vector3(corners[i][0], height * 0.5f, corners[i][1]);
                var postRenderer = post.GetComponent<MeshRenderer>();
                if (postRenderer != null) postRenderer.sharedMaterial = postMat;
            }
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
                        BuildWallSegments(path, root.transform, i);
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

        private static void BuildWallSegments(GrayboxPath path, Transform parent, int index)
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

        /// <summary>流式加载：把某一段的搭建结果追加进已在运行的场景，并立即点亮。</summary>
        public static async Task<List<ModelView>> AppendModelsAsync(
            SceneInstance instance, BuildResult build, CancellationToken ct)
        {
            var added = new List<ModelView>();
            if (instance == null || build == null || build.placed == null) return added;

            for (int i = 0; i < build.placed.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                PlacedAsset placed = build.placed[i];
                if (placed == null) continue;
                ModelView view = await CreateModelAsync(placed, instance, ct);
                if (view == null) continue;
                if (view.renderer != null && view.model != null && view.model.material != null)
                    view.renderer.sharedMaterial = view.model.material;
                view.revealed = true;
                instance.models.Add(view);
                added.Add(view);
            }
            return added;
        }

        private static async Task<ModelView> CreateModelAsync(PlacedAsset placed, SceneInstance instance, CancellationToken ct)
        {
            // 当前阶段所有场景对象都只能由 PrimitiveKit 构建。
            // 即使旧存档/远端数据没有 primitive，也补一个安全形态，绝不触碰模型库或 GLB。
            if (string.IsNullOrEmpty(placed.primitive))
                placed.primitive = placed.kind == SlotKind.Building ? "row_house" : "prop";
            return await Task.FromResult(CreatePrimitiveModel(placed, instance));

#if EPICSPARK_ENABLE_LEGACY_ASSETS
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
            float scaleY;
            Vector3 scale;
            if (placed.height > 0.5f && bounds.size.y > 0.05f)
            {
                // 剧情给了高度（"高 12 米"）：横向按占地、纵向按高度分别缩放。
                // 以前一律等比缩放，30×35 的教堂和 8×8 的城门都被拉成同一比例，"高度"写了也没用。
                float baseHeight = Mathf.Max(0.0001f, bounds.size.y);
                scaleY = Mathf.Clamp(placed.height / baseHeight, 0.02f, 400f);
                scale = new Vector3(scaleX, scaleY, scaleZ);
            }
            else
            {
                scaleY = Mathf.Clamp((scaleX + scaleZ) * 0.5f, 0.02f, 400f);
                scale = new Vector3(scaleY, scaleY, scaleY);
            }
            go.transform.localScale = scale;

            Vector3 worldPos = GrayboxRenderer.ToWorld(new Vector2(placed.position.x, placed.position.z), 0f);
            float bottomOffset = -bounds.min.y * scaleY;
            go.transform.position = new Vector3(worldPos.x, bottomOffset, worldPos.z);
            go.transform.rotation = Quaternion.Euler(0f, placed.rotationY, 0f);

            CreateWireframe(go, model, instance);

            return new ModelView
            {
                slotId = placed.slotId,
                modelId = placed.modelId,
                gameObject = go,
                renderer = meshRenderer,
                model = model,
                flatMaterial = FlatMaterial,
                distance = Vector3.Distance(instance.spawnPoint, go.transform.position),
            };
#endif
        }

        /// <summary>素模装配：按 primitive 生成程序化几何体（长方体/圆柱/圆锥/楔形/台阶/摊位…）。</summary>
        private static ModelView CreatePrimitiveModel(PlacedAsset placed, SceneInstance instance)
        {
            GameObject go = EpicSpark.SceneGen.PrimitiveKit.Build(placed, instance.modelRoot.transform);
            if (go == null) return null;
            var renderer = go.GetComponentInChildren<MeshRenderer>();
            return new ModelView
            {
                slotId = placed.slotId,
                modelId = placed.modelId,
                gameObject = go,
                renderer = renderer,
                model = null,
                flatMaterial = renderer != null ? renderer.sharedMaterial : null,
                distance = Vector3.Distance(instance.spawnPoint, go.transform.position),
                revealed = true,
            };
        }

        private static ModelView CreateFallbackBox(PlacedAsset placed, SceneInstance instance)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "fallback_" + placed.modelId;
            go.transform.SetParent(instance.modelRoot.transform, false);
            float height = placed.height > 0.5f ? placed.height : Mathf.Max(1f, placed.sizeY * 0.5f);
            go.transform.localScale = new Vector3(placed.sizeX, height, placed.sizeY);
            go.transform.position = new Vector3(placed.position.x, height * 0.5f, placed.position.z);
            go.transform.rotation = Quaternion.Euler(0f, placed.rotationY, 0f);
            var renderer = go.GetComponent<MeshRenderer>();
            if (renderer != null) renderer.sharedMaterial = FlatMaterial;
            return new ModelView
            {
                slotId = placed.slotId,
                modelId = placed.modelId,
                gameObject = go,
                renderer = renderer,
                model = null,
                flatMaterial = FlatMaterial,
                distance = Vector3.Distance(instance.spawnPoint, go.transform.position),
            };
        }

        /// <summary>为模型生成蓝色线的「棱」，作为显现阶段的矢量图。</summary>
        private static void CreateWireframe(GameObject target, GlbModel model, SceneInstance instance)
        {
            if (model.creasedEdges == null || model.creasedEdges.Length < 2) return;
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
        }

        // ─────────────────────────── 锚点与 NPC ───────────────────────────

        private static void CreateAnchors(SceneSpec spec, GrayboxPlan plan, SceneInstance instance)
        {
            if (spec == null || spec.anchors == null) return;
            foreach (SceneAnchorSpec anchor in spec.anchors)
            {
                if (anchor == null) continue;
                Vector3 position = ResolveAnchorPosition(anchor, plan, instance);
                var go = new GameObject("Anchor_" + anchor.anchorId);
                go.transform.SetParent(instance.root.transform, false);
                go.transform.position = position;
                var point = go.AddComponent<AnchorPoint>();
                point.Initialize(anchor);
                instance.anchorObjects.Add(go);
            }
        }

        private static Vector3 ResolveAnchorPosition(SceneAnchorSpec anchor, GrayboxPlan plan, SceneInstance instance)
        {
            float halfW = Mathf.Max(1f, instance.width * 0.5f - 0.5f);
            float halfL = Mathf.Max(1f, instance.length * 0.5f - 0.5f);
            // 规格模式下灰盒的 anchor 槽位就是剧情坐标；优先用它，避免 localPosition 扇形兜底覆盖。
            if (EpicSparkFeatureFlags.PreferPlanUnitPositions && plan != null && plan.slots != null)
            {
                for (int i = 0; i < plan.slots.Count; i++)
                {
                    GrayboxSlot slot = plan.slots[i];
                    if (slot == null || slot.kind != SlotKind.Anchor) continue;
                    if (slot.bindId != anchor.anchorId) continue;
                    // 工具灰盒已完成边界与入口校验；装配层必须原样使用，禁止二次夹取导致脱离载体。
                    return new Vector3(slot.position.x, 0.05f, slot.position.y);
                }
            }
            if (anchor.localPosition != Vector3.zero)
            {
                return new Vector3(
                    Mathf.Clamp(anchor.localPosition.x, -halfW, halfW),
                    0.05f,
                    Mathf.Clamp(anchor.localPosition.z, -halfL, halfL));
            }
            return new Vector3(halfW * 0.55f, 0.05f, halfL * 0.55f);
        }

        private static void CreateNpcs(SceneSpec spec, GrayboxPlan plan, SceneInstance instance)
        {
            if (spec == null || spec.npcs == null) return;
            foreach (NpcSpec npc in spec.npcs)
            {
                if (npc == null) continue;
                var go = new GameObject("NPC_" + npc.npcId);
                go.transform.SetParent(instance.root.transform, false);
                Vector3 npcPosition = new Vector3(npc.localPosition.x, 0f, npc.localPosition.z);
                if (EpicSparkFeatureFlags.PreferPlanUnitPositions && plan != null && plan.slots != null)
                {
                    for (int s = 0; s < plan.slots.Count; s++)
                    {
                        GrayboxSlot slot = plan.slots[s];
                        if (slot == null || slot.kind != SlotKind.Npc || slot.bindId != npc.npcId) continue;
                        npcPosition = new Vector3(slot.position.x, 0f, slot.position.y);
                        break;
                    }
                }
                go.transform.position = npcPosition;

                // 占位身体：没有人物美术资产，用胶囊体保证 NPC 可见、可聚焦
                var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                body.name = "Body";
                body.transform.SetParent(go.transform, false);
                body.transform.localPosition = new Vector3(0f, 0.9f, 0f);
                body.transform.localScale = new Vector3(0.55f, 0.9f, 0.55f);
                var renderer = body.GetComponent<MeshRenderer>();
                if (renderer != null) renderer.sharedMaterial = CreateNpcMaterial(npc);

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
                            route = path.waypoints;
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

            CreateAnchors(spec, plan, instance);
            CreateNpcs(spec, plan, instance);
        }
    }
}
