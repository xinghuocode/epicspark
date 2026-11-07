using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using EpicSpark.AI.SceneAgentTest;
using GLTFast;
using UnityEngine;

namespace EpicSpark.AI.Play
{
    /// <summary>
    /// 预制场景的手工搭建器(与场景Agent无关)。
    /// 平地 + 河流/稻田水面 + 模型库现有 GLB + 端点生成 GLB + 锚点/出生点标记。
    /// </summary>
    public static class PlaySceneBuilder
    {
        public const float GroundSize = 120f;

        public static async Task BuildAsync(PlaySceneDef def, Transform parent, Dictionary<string, string> genPaths)
        {
            var lib = ModelLibrary.Load();

            // ---- 环境: 天空盒 + 线性雾(存进场景文件) ----
            var skyShader = Shader.Find("Skybox/Procedural");
            if (skyShader != null)
            {
                var sky = new Material(skyShader);
                if (sky.HasProperty("_SunSize")) sky.SetFloat("_SunSize", 0.04f);
                if (sky.HasProperty("_AtmosphereThickness")) sky.SetFloat("_AtmosphereThickness", 0.85f);
                RenderSettings.skybox = sky;
            }
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = new Color(0.72f, 0.80f, 0.87f);
            RenderSettings.fogStartDistance = 90f;
            RenderSettings.fogEndDistance = 300f;
            RenderSettings.ambientIntensity = 1.1f;

            // ---- 平行光(场景文件自带光源) ----
            var lightGo = new GameObject("Sun");
            lightGo.transform.SetParent(parent, false);
            lightGo.transform.rotation = Quaternion.Euler(50, -30, 0);
            var sun = lightGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.2f;

            // ---- 地面: 6x6 Plane 网格铺草地贴图(Plane 默认 10x10, x2=20m/块, 贴图 2 重复/块=每 10m 一次) ----
            for (int gx = 0; gx < 6; gx++)
            {
                for (int gz = 0; gz < 6; gz++)
                {
                    var tile = GameObject.CreatePrimitive(PrimitiveType.Plane);
                    tile.name = $"ground_{gx}_{gz}";
                    tile.transform.SetParent(parent, false);
                    tile.transform.position = new Vector3(-50 + gx * 20, 0, -50 + gz * 20);
                    tile.transform.localScale = Vector3.one * 2f;
                    tile.GetComponent<Renderer>().sharedMaterial = GrassMat;
                }
            }

            // ---- 水面(河流 或 稻田) ----
            if (def.riverNorth)
            {
                var water = GameObject.CreatePrimitive(PrimitiveType.Cube);
                water.name = "river";
                water.transform.SetParent(parent, false);
                water.transform.position = new Vector3(0, 0.05f, -40);
                water.transform.localScale = new Vector3(GroundSize, 0.1f, 20);
                DestroyCollider(water);
                water.GetComponent<Renderer>().sharedMaterial = WaterMat;
            }
            if (def.paddyField)
            {
                // 4 块潴水稻田: 下陷泥底 + 水面 + 秧苗行
                var paddyPos = new[] { new Vector3(-24, 0, -8), new Vector3(2, 0, -8), new Vector3(28, 0, -8),
                                       new Vector3(-24, 0, -24), new Vector3(2, 0, -24), new Vector3(28, 0, -24) };
                foreach (var pp in paddyPos)
                {
                    // 泥底(下陷, 田埂即周围地面)
                    var mud = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    mud.name = "paddy_mud";
                    mud.transform.SetParent(parent, false);
                    mud.transform.position = new Vector3(pp.x, -0.15f, pp.z);
                    mud.transform.localScale = new Vector3(19f, 0.3f, 11f);
                    mud.GetComponent<Renderer>().sharedMaterial = MudMat;

                    // 水面(略高于泥底)
                    var water = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    water.name = "paddy_water";
                    water.transform.SetParent(parent, false);
                    water.transform.position = new Vector3(pp.x, 0.04f, pp.z);
                    water.transform.localScale = new Vector3(18.6f, 0.06f, 10.6f);
                    DestroyCollider(water);
                    water.GetComponent<Renderer>().sharedMaterial = PaddyMat;

                    // 稻子(1.6m 株距, 交叉面片植被卡片, 无碰撞)
                    for (float sx = -8; sx <= 8; sx += 1.6f)
                    {
                        for (float sz = -4; sz <= 4; sz += 1.6f)
                        {
                            var rice = new GameObject("rice");
                            rice.transform.SetParent(parent, false);
                            rice.transform.position = new Vector3(pp.x + sx, 0.02f, pp.z + sz);
                            rice.transform.rotation = Quaternion.Euler(0, (sx * 7 + sz * 13) % 360, 0);
                            rice.AddComponent<MeshFilter>().sharedMesh = RiceMesh;
                            rice.AddComponent<MeshRenderer>().sharedMaterial = RiceMat;
                        }
                    }
                }
            }

            // ---- 土路条带(贴地装饰, 无碰撞) ----
            foreach (var p in def.paths)
            {
                var strip = GameObject.CreatePrimitive(PrimitiveType.Cube);
                strip.name = "path";
                DestroyCollider(strip);
                strip.transform.SetParent(parent, false);
                strip.transform.position = new Vector3(p.pos.x, 0.02f, p.pos.z);
                strip.transform.localScale = new Vector3(p.size.x, 0.04f, p.size.y);
                strip.GetComponent<Renderer>().sharedMaterial = DirtMat;
            }

            // ---- 模型库现有模型 ----
            foreach (var m in def.models)
            {
                if (!lib.TryGet(m.modelId, out var entry)) continue;
                await PlaceGlbAsync(entry.glb, m.pos, m.targetSize, m.rotY, parent, m.modelId);
            }

            // ---- 端点生成的模型 ----
            foreach (var g in def.genModels)
            {
                if (string.IsNullOrEmpty(g.key) || !genPaths.TryGetValue(g.key, out var path) || string.IsNullOrEmpty(path))
                    continue;
                await PlaceGlbAsync(path, g.pos, g.targetSize, g.rotY, parent, g.key);
                if (g.extraPositions != null)
                    foreach (var ep in g.extraPositions)
                        await PlaceGlbAsync(path, ep, g.targetSize, g.rotY, parent, g.key);
            }

            // ---- 出生点标记 ----
            var spawn = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            spawn.name = "spawn_point";
            DestroyCollider(spawn);
            spawn.transform.SetParent(parent, false);
            spawn.transform.position = def.playerSpawn + Vector3.down * 0.9f;
            spawn.transform.localScale = new Vector3(1.2f, 0.1f, 1.2f);
            spawn.GetComponent<Renderer>().sharedMaterial = MakeLit(Color.green);

            // ---- 锚点标记 ----
            foreach (var a in def.anchors)
            {
                var marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                marker.name = "anchor_" + a.name;
                DestroyCollider(marker);
                marker.transform.SetParent(parent, false);
                marker.transform.position = a.pos + Vector3.up * 2.5f;
                marker.transform.localScale = Vector3.one * 1.2f;
                marker.GetComponent<Renderer>().sharedMaterial = MakeLit(Color.yellow);
                AddLabel(marker.transform, Vector3.up * 1.6f, "⛳ " + a.name, Color.yellow);
            }
        }

        static async Task PlaceGlbAsync(string glbPath, Vector3 pos, float targetSize, float rotY, Transform parent, string name)
        {
            GameObject holder = null;

#if UNITY_EDITOR
            // 优先用 glTFast Editor 导入的 prefab 资产(工程内路径时):
            // 场景只存 prefab 引用, 避免运行时实例化把 Mesh 数据嵌进场景文件(否则单场景达数百 MB)。
            if (!Path.IsPathRooted(glbPath))
            {
                var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(glbPath);
                if (prefab != null)
                    holder = (GameObject)UnityEditor.PrefabUtility.InstantiatePrefab(prefab);
            }
#endif
            if (holder == null)
            {
                var gltf = await ModelGltfCache.LoadAsync(glbPath);
                if (gltf == null) return;
                holder = new GameObject("model_" + name);
                holder.transform.SetParent(parent, false);
                if (!await gltf.InstantiateMainSceneAsync(holder.transform))
                {
                    Object.Destroy(holder);
                    return;
                }
            }

            holder.name = "model_" + name;
            holder.transform.SetParent(parent, false);

            // 缩放
            var renderers = holder.GetComponentsInChildren<Renderer>();
            if (renderers.Length > 0)
            {
                var b = renderers[0].bounds;
                foreach (var r in renderers) b.Encapsulate(r.bounds);
                float maxDim = Mathf.Max(b.size.x, b.size.y, b.size.z);
                if (maxDim > 0.001f) holder.transform.localScale = Vector3.one * (targetSize / maxDim);
            }

            holder.transform.rotation = Quaternion.Euler(0, rotY, 0);

            // 落地: 模型枢轴常在几何中心, 直接放 y=0 会有一半埋进地里;
            // 按缩放后的包围盒把底面抬到地面
            if (renderers.Length > 0)
            {
                var b = renderers[0].bounds;
                foreach (var r in renderers) b.Encapsulate(r.bounds);
                holder.transform.position = new Vector3(pos.x, pos.y - b.min.y, pos.z);
            }
            else
            {
                holder.transform.position = pos;
            }

            // 碰撞盒: glTFast 默认不生成碰撞, 给每个网格加 MeshCollider(静态场景用)
            foreach (var mf in holder.GetComponentsInChildren<MeshFilter>())
            {
                if (mf.sharedMesh == null) continue;
                var mc = mf.gameObject.AddComponent<MeshCollider>();
                mc.sharedMesh = mf.sharedMesh;
            }
        }

        static void AddLabel(Transform parent, Vector3 localOffset, string text, Color color)
        {
            var go = new GameObject("label");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localOffset;
            var tm = go.AddComponent<TextMesh>();
            tm.text = text;
            tm.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            tm.fontSize = 48;
            tm.characterSize = 0.12f;
            tm.anchor = TextAnchor.MiddleCenter;
            tm.color = color;
            go.GetComponent<MeshRenderer>().sharedMaterial = tm.font.material;
        }

        static void DestroyCollider(GameObject go)
        {
            var c = go.GetComponent<Collider>();
            if (c == null) return;
#if UNITY_EDITOR
            if (!UnityEditor.EditorApplication.isPlaying) { Object.DestroyImmediate(c); return; }
#endif
            Object.Destroy(c);
        }

        static readonly Dictionary<Color, Material> matCache = new Dictionary<Color, Material>();
        static Material MakeLit(Color c)
        {
            if (matCache.TryGetValue(c, out var m) && m != null) return m;
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            m = new Material(shader) { color = c };
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            matCache[c] = m;
            return m;
        }

        const string GrassTexturePath = "Assets/Art/Textures/grass_tile.png";

        static Texture2D GrassTexture => LoadTextureAsset(GrassTexturePath);

        static Material s_grass;
        static Material GrassMat
        {
            get
            {
                if (s_grass != null) return s_grass;
                var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                s_grass = new Material(shader) { color = Color.white };
                if (s_grass.HasProperty("_BaseColor")) s_grass.SetColor("_BaseColor", Color.white);
                var tex = GrassTexture;
                if (tex != null && s_grass.HasProperty("_BaseMap"))
                {
                    s_grass.SetTexture("_BaseMap", tex);
                    s_grass.SetTextureScale("_BaseMap", new Vector2(2f, 2f)); // 每 10m 一次重复
                }
                return s_grass;
            }
        }

        static Material s_mud, s_dirt;
        static Material MudMat => s_mud ?? (s_mud = MakeLit(new Color(0.32f, 0.24f, 0.16f)));
        static Material DirtMat
        {
            get
            {
                if (s_dirt != null) return s_dirt;
                var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                s_dirt = new Material(shader) { color = Color.white };
                if (s_dirt.HasProperty("_BaseColor")) s_dirt.SetColor("_BaseColor", Color.white);
                var tex = LoadTextureAsset("Assets/Art/Textures/dirt_tile.png");
                if (tex != null && s_dirt.HasProperty("_BaseMap"))
                {
                    s_dirt.SetTexture("_BaseMap", tex);
                    s_dirt.SetTextureScale("_BaseMap", new Vector2(3f, 3f)); // 每 ~7m 一次重复
                }
                return s_dirt;
            }
        }

        // ==================== 稻子(交叉面片 + alpha 裁剪) ====================

        static Mesh s_riceMesh;
        static Mesh RiceMesh
        {
            get
            {
                if (s_riceMesh != null) return s_riceMesh;
                var verts = new List<Vector3>();
                var uvs = new List<Vector2>();
                var tris = new List<int>();
                for (int i = 0; i < 3; i++) // 3 片面片, 60° 交叉
                {
                    float ang = i * 60f * Mathf.Deg2Rad;
                    var dir = new Vector3(Mathf.Cos(ang), 0, Mathf.Sin(ang)) * 0.38f;
                    int b = verts.Count;
                    verts.Add(-dir); verts.Add(dir);
                    verts.Add(-dir + Vector3.up); verts.Add(dir + Vector3.up);
                    uvs.Add(new Vector2(0, 0)); uvs.Add(new Vector2(1, 0));
                    uvs.Add(new Vector2(0, 1)); uvs.Add(new Vector2(1, 1));
                    tris.Add(b); tris.Add(b + 2); tris.Add(b + 1);
                    tris.Add(b + 1); tris.Add(b + 2); tris.Add(b + 3);
                }
                var m = new Mesh { vertices = verts.ToArray(), uv = uvs.ToArray(), triangles = tris.ToArray() };
                m.RecalculateNormals();
                m.RecalculateBounds();
                s_riceMesh = m;
                return m;
            }
        }

        static Material s_riceMat;
        static Material RiceMat
        {
            get
            {
                if (s_riceMat != null) return s_riceMat;
                var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                s_riceMat = new Material(shader) { color = Color.white };
                if (s_riceMat.HasProperty("_BaseColor")) s_riceMat.SetColor("_BaseColor", Color.white);
                var tex = LoadTextureAsset("Assets/Art/Textures/rice_plant.png");
                if (tex != null && s_riceMat.HasProperty("_BaseMap"))
                    s_riceMat.SetTexture("_BaseMap", tex);
                if (s_riceMat.HasProperty("_AlphaClip")) s_riceMat.SetFloat("_AlphaClip", 1f); // alpha 裁剪
                if (s_riceMat.HasProperty("_Cutoff")) s_riceMat.SetFloat("_Cutoff", 0.4f);
                if (s_riceMat.HasProperty("_Cull")) s_riceMat.SetFloat("_Cull", 0f);          // 双面渲染
                if (s_riceMat.HasProperty("_Smoothness")) s_riceMat.SetFloat("_Smoothness", 0f);
                return s_riceMat;
            }
        }

        // ==================== 通用贴图加载(资产路径) ====================

        static readonly Dictionary<string, Texture2D> texCache = new Dictionary<string, Texture2D>();
        static Texture2D LoadTextureAsset(string path)
        {
            if (texCache.TryGetValue(path, out var t) && t != null) return t;
#if UNITY_EDITOR
            t = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (t != null) { texCache[path] = t; return t; }
#endif
            string full = Path.GetFullPath(Path.Combine(Application.dataPath, "..", path));
            if (File.Exists(full))
            {
                t = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                t.LoadImage(File.ReadAllBytes(full));
                texCache[path] = t;
            }
            return t;
        }

        static Material s_water, s_paddy;
        static Material WaterMat
        {
            get
            {
                if (s_water == null)
                {
                    s_water = MakeLit(new Color(0.2f, 0.45f, 0.7f, 0.6f));
                    SetTransparent(s_water);
                }
                return s_water;
            }
        }
        static Material PaddyMat
        {
            get
            {
                if (s_paddy == null)
                {
                    s_paddy = MakeLit(new Color(0.35f, 0.55f, 0.35f, 0.75f));
                    SetTransparent(s_paddy);
                }
                return s_paddy;
            }
        }

        static void SetTransparent(Material m)
        {
            if (m.HasProperty("_Surface")) m.SetFloat("_Surface", 1f);
            if (m.HasProperty("_Blend")) m.SetFloat("_Blend", 0f);
            if (m.HasProperty("_ZWrite")) m.SetFloat("_ZWrite", 0f);
            m.renderQueue = 3000;
        }
    }
}
