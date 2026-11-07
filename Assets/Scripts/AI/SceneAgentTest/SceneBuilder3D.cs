using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using GLTFast;
using UnityEngine;

namespace EpicSpark.AI.SceneAgentTest
{
    /// <summary>
    /// 场景3D构建器: 订阅 SceneWorld.Changed, 把场景Agent的工具调用结果实时构建为真实 Unity 场景。
    ///   init_terrain            → UnityEngine.Terrain(高度场取自 SceneWorld.TerrainHeight 高斯函数)
    ///   place_model             → glTFast 异步加载 GLB, 按 size_x/size_y 缩放, 底面贴合地形高度
    ///   place_bar_shaped_object → 沿节点的墙/栅栏/道路/战壕实体(Cube 拉伸)
    ///   set_auchor_point        → 黄色标记球 + 悬浮锚点名称
    ///   set_start_point         → 绿色起点标记 + 悬浮"起点"文字
    /// 每次变更整体重建(模型 GLB 按 id 缓存, 重建只实例化不重复加载)。
    /// </summary>
    public class SceneBuilder3D : IDisposable
    {
        const int HeightmapResolution = 257;

        readonly SceneWorld world;
        readonly GameObject root;
        readonly List<TerrainData> terrainDataToDestroy = new List<TerrainData>();
        int buildVersion;
        bool cameraAdjusted;

        // ---- 材质缓存 ----
        static Material s_terrainMat, s_wallMat, s_fenceMat, s_roadMat, s_trenchMat, s_anchorMat, s_startMat;
        static Font s_builtinFont;

        SceneBuilder3D(SceneWorld world)
        {
            this.world = world;
            root = new GameObject("Scene3D");
            world.Changed += OnWorldChanged;
            Rebuild();
        }

        public static SceneBuilder3D Attach(SceneWorld world) => new SceneBuilder3D(world);

        public void Dispose()
        {
            world.Changed -= OnWorldChanged;
            foreach (var td in terrainDataToDestroy) UnityEngine.Object.Destroy(td);
            terrainDataToDestroy.Clear();
            if (root != null) UnityEngine.Object.Destroy(root);
        }

        void OnWorldChanged(SceneWorld w) => Rebuild();

        // ================== 重建 ==================

        void Rebuild()
        {
            int version = ++buildVersion;

            // 清空旧的 3D 内容(保留 root 本身)
            foreach (var td in terrainDataToDestroy) UnityEngine.Object.Destroy(td);
            terrainDataToDestroy.Clear();
            for (int i = root.transform.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(root.transform.GetChild(i).gameObject);

            if (world.IsTerrainInitialized) BuildTerrain();
            BuildWater();
            BuildBarObjects();
            BuildMarkers();
            BuildModelsAsync(version); // 异步: GLB 加载较慢
            AdjustCameraOnce();
        }

        /// <summary>首次重建后把主相机移到场景俯瞰位(只调一次, 不打扰用户后续手动操作)。</summary>
        void AdjustCameraOnce()
        {
            if (cameraAdjusted) return;
            cameraAdjusted = true;
            var cam = Camera.main;
            if (cam == null) return;
            float size = Mathf.Max(world.Width, world.Length);
            var center = new Vector3(world.Width * 0.5f, 0f, world.Length * 0.5f);
            cam.transform.position = center + new Vector3(0f, size * 0.9f, -size * 0.75f);
            cam.transform.LookAt(center);
        }

        bool IsStale(int version) => version != buildVersion;

        // ================== 地形 ==================

        void BuildTerrain()
        {
            int res = HeightmapResolution;
            float[,] heights = new float[res, res];
            double minH = double.MaxValue, maxH = double.MinValue;

            // 第一遍: 采样并求 min/max, 归一化到 0..1
            for (int iz = 0; iz < res; iz++)
            {
                double z = world.Length * iz / (double)(res - 1);
                for (int ix = 0; ix < res; ix++)
                {
                    double x = world.Width * ix / (double)(res - 1);
                    double h = world.TerrainHeight(x, z);
                    if (h < minH) minH = h;
                    if (h > maxH) maxH = h;
                    heights[iz, ix] = (float)h;
                }
            }
            double range = Math.Max(maxH - minH, 0.01);
            for (int iz = 0; iz < res; iz++)
                for (int ix = 0; ix < res; ix++)
                    heights[iz, ix] = (float)((heights[iz, ix] - minH) / range);

            var terrainData = new TerrainData
            {
                heightmapResolution = res,
                size = new Vector3(world.Width, (float)range, world.Length),
            };
            terrainData.terrainLayers = new[] { CreateTerrainLayer() };
            terrainData.SetHeights(0, 0, heights);

            var terrainGo = Terrain.CreateTerrainGameObject(terrainData);
            terrainGo.name = "Terrain";
            terrainGo.transform.SetParent(root.transform, false);
            terrainGo.transform.position = new Vector3(0, (float)minH, 0);
            var terrain = terrainGo.GetComponent<Terrain>();
            terrain.materialTemplate = TerrainMaterial;
            terrain.heightmapPixelError = 1f;
            terrainDataToDestroy.Add(terrainData);
        }

        static TerrainLayer CreateTerrainLayer()
        {
            var layer = new TerrainLayer { diffuseTexture = Texture2D.whiteTexture };
            layer.tileSize = new Vector2(16, 16);
            return layer;
        }

        // ================== 河流水面 ==================

        /// <summary>沿河流折线铺半透明水面, 水位取河道最大深度的 35%(河道底部仍低于水位)。</summary>
        void BuildWater()
        {
            foreach (var river in world.Rivers)
            {
                var nodes = river.nodes;
                if (nodes == null || nodes.Count < 2) continue;

                double maxDepth = 0;
                foreach (var n in nodes) maxDepth = Math.Max(maxDepth, n[2]);
                if (maxDepth <= 0.01) continue;
                float waterY = (float)(-maxDepth * 0.35);

                for (int i = 0; i < nodes.Count - 1; i++)
                {
                    var a = nodes[i];
                    var b = nodes[i + 1];
                    float ax = (float)a[0], az = (float)a[1];
                    float bx = (float)b[0], bz = (float)b[1];
                    float dx = bx - ax, dz = bz - az;
                    float len = Mathf.Sqrt(dx * dx + dz * dz);
                    if (len < 0.01f) continue;

                    float width = ((float)a[3] + (float)b[3]) * 0.5f * 1.6f; // 略宽于河道
                    var seg = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    seg.name = $"water_{i}";
                    DestroyCollider(seg);
                    seg.transform.SetParent(root.transform, false);
                    seg.transform.position = new Vector3((ax + bx) * 0.5f, waterY, (az + bz) * 0.5f);
                    seg.transform.rotation = Quaternion.Euler(0, -Mathf.Atan2(dz, dx) * Mathf.Rad2Deg, 0);
                    seg.transform.localScale = new Vector3(len, 0.06f, width);
                    seg.GetComponent<Renderer>().sharedMaterial = WaterMaterial;
                }
            }
        }

        // ================== 条状物体 ==================

        void BuildBarObjects()
        {
            foreach (var bar in world.BarObjects)
            {
                var nodes = bar.nodes;
                for (int i = 0; i < nodes.Count - 1; i++)
                {
                    var a = nodes[i];
                    var b = nodes[i + 1];
                    float ax = (float)a[0], az = (float)a[1], aw = (float)a[2];
                    float bx = (float)b[0], bz = (float)b[1];
                    float dx = bx - ax, dz = bz - az;
                    float len = Mathf.Sqrt(dx * dx + dz * dz);
                    if (len < 0.01f) continue;

                    float midX = (ax + bx) * 0.5f, midZ = (az + bz) * 0.5f;
                    float groundY = (float)world.TerrainHeight(midX, midZ);
                    float width = (aw + (float)b[2]) * 0.5f;

                    float height;
                    float baseY;
                    Material mat;
                    switch (bar.type)
                    {
                        case "wall": height = 3f; baseY = groundY; mat = WallMaterial; break;
                        case "fence": height = 1.5f; baseY = groundY; mat = FenceMaterial; break;
                        case "road": height = 0.12f; baseY = groundY + 0.06f; mat = RoadMaterial; break;
                        case "trench":
                            // 沟体由 SceneWorld.TerrainHeight 真实下切 1.8m 形成(groundY 已是沟底高度);
                            // 这里只铺一条略高于沟底的深色底板, 模拟壕底
                            height = 0.12f; baseY = groundY + 0.02f; mat = TrenchMaterial; break;
                        default: continue;
                    }

                    var seg = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    seg.name = $"bar_{bar.type}_{i}";
                    DestroyCollider(seg);
                    seg.transform.SetParent(root.transform, false);
                    seg.transform.position = new Vector3(midX, baseY + height * 0.5f, midZ);
                    seg.transform.rotation = Quaternion.Euler(0, -Mathf.Atan2(dz, dx) * Mathf.Rad2Deg, 0);
                    seg.transform.localScale = new Vector3(len, height, width);
                    seg.GetComponent<Renderer>().sharedMaterial = mat;
                }
            }
        }

        // ================== 锚点 / 起点标记 ==================

        void BuildMarkers()
        {
            foreach (var kv in world.AnchorBindings)
            {
                var placed = world.PlacedModels;
                SceneWorld.PlacedModel target = null;
                foreach (var p in placed)
                    if (p.placedName == kv.Value) { target = p; break; }
                if (target == null) continue;

                float x = (float)target.x, z = (float)target.y;
                float y = (float)world.TerrainHeight(x, z);
                var marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                marker.name = $"anchor_{kv.Key}";
                DestroyCollider(marker);
                marker.transform.SetParent(root.transform, false);
                marker.transform.position = new Vector3(x, y + 4f, z);
                marker.transform.localScale = Vector3.one * 1.2f;
                marker.GetComponent<Renderer>().sharedMaterial = AnchorMaterial;
                AddLabel(marker.transform, Vector3.up * 1.6f, "⛳ " + kv.Key, Color.yellow);
            }

            if (world.HasStartPoint)
            {
                float x = (float)world.StartX, z = (float)world.StartY;
                float y = (float)world.TerrainHeight(x, z);
                var marker = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                marker.name = "start_point";
                DestroyCollider(marker);
                marker.transform.SetParent(root.transform, false);
                marker.transform.position = new Vector3(x, y + 1f, z);
                marker.transform.localScale = new Vector3(1.2f, 1f, 1.2f);
                marker.GetComponent<Renderer>().sharedMaterial = StartMaterial;
                AddLabel(marker.transform, Vector3.up * 2.2f, "起点", Color.green);
            }
        }

        static void AddLabel(Transform parent, Vector3 localOffset, string text, Color color)
        {
            var go = new GameObject("label");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localOffset;
            var tm = go.AddComponent<TextMesh>();
            tm.text = text;
            tm.font = BuiltinFont;
            tm.fontSize = 48;
            tm.characterSize = 0.12f;
            tm.anchor = TextAnchor.MiddleCenter;
            tm.color = color;
            tm.GetComponent<MeshRenderer>().sharedMaterial = tm.font.material;
        }

        // ================== 模型(GLB) ==================

        async void BuildModelsAsync(int version)
        {
            // 快照列表: 遍历时主线程可能仍在添加新放置(工具调用), 直接枚举会抛 Collection was modified
            var models = new List<SceneWorld.PlacedModel>(world.PlacedModels);
            foreach (var p in models)
            {
                if (IsStale(version)) return;
                if (!world.Library.TryGet(p.modelId, out var entry) || string.IsNullOrEmpty(entry.glb)) continue;

                try
                {
                    var gltf = await ModelGltfCache.LoadAsync(entry.glb);
                    if (gltf == null || IsStale(version)) continue;

                    var holder = new GameObject($"model_{p.placedName}");
                    holder.transform.SetParent(root.transform, false);
                    bool ok = await gltf.InstantiateMainSceneAsync(holder.transform);
                    if (!ok || IsStale(version)) { UnityEngine.Object.Destroy(holder); continue; }

                    // 按 size_x/size_y 缩放(X→世界X, Y→世界Z), 底面贴合地形
                    Bounds b = CalcBounds(holder);
                    if (b.size.x > 0.001f && b.size.z > 0.001f)
                    {
                        float sx = (float)p.sizeX / b.size.x;
                        float sz = (float)p.sizeY / b.size.z;
                        var scale = new Vector3(sx, (sx + sz) * 0.5f, sz);
                        holder.transform.localScale = scale;
                        float groundY = (float)world.TerrainHeight(p.x, p.y);
                        holder.transform.position = new Vector3((float)p.x, groundY - b.min.y * scale.y, (float)p.y);
                    }
                    else
                    {
                        holder.transform.position = new Vector3((float)p.x, (float)world.TerrainHeight(p.x, p.y), (float)p.y);
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[SceneBuilder3D] 模型 {p.modelId} 加载失败: {e.Message}");
                }
            }
        }

        static Bounds CalcBounds(GameObject go)
        {
            var renderers = go.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) return new Bounds(go.transform.position, Vector3.zero);
            var b = renderers[0].bounds;
            foreach (var r in renderers) b.Encapsulate(r.bounds);
            return b;
        }

        static void DestroyCollider(GameObject go)
        {
            var c = go.GetComponent<Collider>();
            if (c != null) UnityEngine.Object.Destroy(c);
        }

        // ================== 资源缓存 ==================

        static Material MakeLitMaterial(Color color)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            var m = new Material(shader) { color = color };
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
            return m;
        }

        static Material TerrainMaterial =>
            s_terrainMat ?? (s_terrainMat = MakeLitMaterial(new Color(0.45f, 0.55f, 0.35f)));

        static Material WallMaterial =>
            s_wallMat ?? (s_wallMat = MakeLitMaterial(new Color(0.6f, 0.58f, 0.55f)));
        static Material FenceMaterial =>
            s_fenceMat ?? (s_fenceMat = MakeLitMaterial(new Color(0.45f, 0.33f, 0.2f)));
        static Material RoadMaterial =>
            s_roadMat ?? (s_roadMat = MakeLitMaterial(new Color(0.32f, 0.3f, 0.28f)));
        static Material TrenchMaterial =>
            s_trenchMat ?? (s_trenchMat = MakeLitMaterial(new Color(0.2f, 0.15f, 0.1f)));
        static Material AnchorMaterial =>
            s_anchorMat ?? (s_anchorMat = MakeLitMaterial(Color.yellow));
        static Material StartMaterial =>
            s_startMat ?? (s_startMat = MakeLitMaterial(Color.green));

        static Material s_waterMat;
        static Material WaterMaterial
        {
            get
            {
                if (s_waterMat != null) return s_waterMat;
                var m = MakeLitMaterial(new Color(0.2f, 0.5f, 0.8f, 0.55f));
                // URP Lit 切换为透明表面
                if (m.HasProperty("_Surface")) m.SetFloat("_Surface", 1f);
                if (m.HasProperty("_Blend")) m.SetFloat("_Blend", 0f);
                if (m.HasProperty("_ZWrite")) m.SetFloat("_ZWrite", 0f);
                if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.9f);
                m.renderQueue = 3000;
                s_waterMat = m;
                return m;
            }
        }

        static Font BuiltinFont =>
            s_builtinFont ?? (s_builtinFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));
    }

    /// <summary>GLB 模型加载缓存(按工程内路径), 避免重复解析。</summary>
    static class ModelGltfCache
    {
        static readonly Dictionary<string, Task<GltfImport>> cache = new Dictionary<string, Task<GltfImport>>();

        public static Task<GltfImport> LoadAsync(string glbPath)
        {
            if (cache.TryGetValue(glbPath, out var task)) return task;

            task = LoadInner(glbPath);
            cache[glbPath] = task;
            return task;
        }

        static async Task<GltfImport> LoadInner(string glbPath)
        {
            // 支持两种路径: 工程内相对路径(缓存库模型) 或 绝对路径(运行时生成的模型)
            string fullPath = Path.IsPathRooted(glbPath)
                ? glbPath
                : Path.GetFullPath(Path.Combine(Application.dataPath, "..", glbPath));
            if (!File.Exists(fullPath))
            {
                Debug.LogWarning("[SceneBuilder3D] GLB 文件不存在: " + fullPath);
                return null;
            }
            var gltf = new GltfImport();
            string uri = new Uri(fullPath).AbsoluteUri;
            bool ok = await gltf.Load(uri);
            if (!ok)
            {
                Debug.LogWarning("[SceneBuilder3D] GLB 解析失败: " + fullPath);
                gltf.Dispose();
                return null;
            }
            return gltf;
        }
    }
}
