// 运行时 GLB 加载器。
// Unity 2022 不原生支持 .glb，且本项目不引入任何第三方包，因此这里直接解析 glTF 2.0 二进制容器。
// 支持本项目资产的实际子集：单 primitive、POSITION + TEXCOORD_0、uint32 索引、内嵌 PNG、PBR baseColorTexture。
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace EpicSpark.Loading
{
    /// <summary>加载完成的一个模型实例数据（可被多个 GameObject 复用）。</summary>
    public class GlbModel
    {
        public string sourceFile = "";
        public Mesh mesh;
        public Material material;
        public Texture2D texture;
        public Bounds bounds;
        /// <summary>true 表示原点在底部（manifest 的 pivot=bottom）。</summary>
        public bool pivotBottom;
        /// <summary>棱线（折边）索引，供蓝绿线框阶段使用。</summary>
        public int[] creasedEdges;
    }

    /// <summary>GLB 解析与缓存。所有美术资产都通过它进入引擎。</summary>
    public static class GlbModelLoader
    {
        public const string ModelFolder = "Models";
        private static readonly Dictionary<string, GlbModel> Cache = new Dictionary<string, GlbModel>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Task<GlbModel>> InFlight = new Dictionary<string, Task<GlbModel>>(StringComparer.OrdinalIgnoreCase);
        private static readonly object Gate = new object();

        public static int CachedCount { get { lock (Gate) { return Cache.Count; } } }

        public static void ClearCache()
        {
            lock (Gate) { Cache.Clear(); InFlight.Clear(); }
        }

        /// <summary>按文件名加载（例如 mdl_church_001.glb）。带缓存与并发去重。</summary>
        public static Task<GlbModel> LoadAsync(string fileName, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(fileName)) return Task.FromResult<GlbModel>(null);
            string key = Path.GetFileName(fileName);
            lock (Gate)
            {
                GlbModel cached;
                if (Cache.TryGetValue(key, out cached)) return Task.FromResult(cached);
                Task<GlbModel> running;
                if (InFlight.TryGetValue(key, out running)) return running;
                var task = LoadInternalAsync(key, ct);
                InFlight[key] = task;
                return task;
            }
        }

        private static async Task<GlbModel> LoadInternalAsync(string fileName, CancellationToken ct)
        {
            GlbModel model = null;
            try
            {
                byte[] bytes = await ReadFileAsync(fileName, ct);
                if (bytes == null || bytes.Length < 20)
                {
                    Debug.LogWarning("[GLB] 文件为空或过小: " + fileName);
                }
                else
                {
                    model = Parse(fileName, bytes);
                    lock (Gate) { Cache[fileName] = model; }
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[GLB] 加载失败 " + fileName + " : " + e.Message);
                model = null;
            }
            finally
            {
                lock (Gate) { InFlight.Remove(fileName); }
            }
            return model;
        }

        /// <summary>
        /// 读取模型文件。依次尝试：绝对路径 → StreamingAssets/Models → 运行时生成的资产目录
        /// （persistentDataPath/EpicSpark/generated）。兼容 Android 的 jar 路径。
        /// </summary>
        public static async Task<byte[]> ReadFileAsync(string fileName, CancellationToken ct)
        {
            string[] candidates = ResolveCandidates(fileName);
            for (int i = 0; i < candidates.Length; i++)
            {
                string candidate = candidates[i];
                if (string.IsNullOrEmpty(candidate)) continue;
                bool needsRequest = candidate.Contains("://") || candidate.Contains("jar:");
                if (needsRequest) continue;
                if (!File.Exists(candidate)) continue;
                try { return File.ReadAllBytes(candidate); }
                catch (Exception e) { Debug.LogWarning("[GLB] 直接读取失败，改用 UnityWebRequest: " + e.Message); }
            }

            // 走到这里说明本地都没有，交给 UnityWebRequest（处理 jar:// 或远端 URL）
            string path = candidates.Length > 0 && !string.IsNullOrEmpty(candidates[0])
                ? candidates[0]
                : Path.Combine(Application.streamingAssetsPath, ModelFolder, fileName);
            string url = path.Replace('\\', '/');
            if (!url.Contains("://")) url = "file:///" + url;

            using (var request = UnityWebRequest.Get(url))
            {
                request.timeout = 30;
                UnityWebRequestAsyncOperation op = request.SendWebRequest();
                while (!op.isDone)
                {
                    if (ct.IsCancellationRequested) { request.Abort(); return null; }
                    await Task.Yield();
                }
                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning("[GLB] 下载失败 " + fileName + " : " + request.error);
                    return null;
                }
                return request.downloadHandler != null ? request.downloadHandler.data : null;
            }
        }

        /// <summary>候选路径：绝对路径优先，其次内置模型库，最后是运行时生成的资产。</summary>
        private static string[] ResolveCandidates(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return new string[0];
            string name = Path.GetFileName(fileName);
            string generated = Path.Combine(Application.persistentDataPath, "EpicSpark", "generated", name);
            if (Path.IsPathRooted(fileName))
            {
                return new[]
                {
                    fileName,
                    Path.Combine(Application.streamingAssetsPath, ModelFolder, name),
                    generated,
                };
            }
            return new[]
            {
                Path.Combine(Application.streamingAssetsPath, ModelFolder, name),
                generated,
                fileName,
            };
        }

        // ─────────────────────────── 解析 ───────────────────────────

        public static GlbModel Parse(string fileName, byte[] bytes)
        {
            int magic = BitConverter.ToInt32(bytes, 0);
            if (magic != 0x46546C67) throw new InvalidDataException("不是 GLB 文件（magic 不匹配）");
            int version = BitConverter.ToInt32(bytes, 4);
            if (version != 2) throw new InvalidDataException("仅支持 glTF 2.0，实际版本 " + version);

            string json = null;
            byte[] bin = null;
            int offset = 12;
            while (offset + 8 <= bytes.Length)
            {
                int chunkLength = BitConverter.ToInt32(bytes, offset);
                int chunkType = BitConverter.ToInt32(bytes, offset + 4);
                int dataStart = offset + 8;
                if (chunkLength < 0 || dataStart + chunkLength > bytes.Length) break;
                if (chunkType == 0x4E4F534A) // JSON
                    json = System.Text.Encoding.UTF8.GetString(bytes, dataStart, chunkLength);
                else if (chunkType == 0x004E4942) // BIN
                {
                    bin = new byte[chunkLength];
                    Buffer.BlockCopy(bytes, dataStart, bin, 0, chunkLength);
                }
                offset = dataStart + chunkLength;
            }

            if (json == null) throw new InvalidDataException("缺少 JSON 块");
            var root = JsonUtility.FromJson<GlbRoot>(json);
            if (root == null || root.meshes == null || root.meshes.Length == 0)
                throw new InvalidDataException("没有 mesh");

            GlbPrimitive primitive = null;
            for (int m = 0; m < root.meshes.Length && primitive == null; m++)
            {
                if (root.meshes[m] != null && root.meshes[m].primitives != null && root.meshes[m].primitives.Length > 0)
                    primitive = root.meshes[m].primitives[0];
            }
            if (primitive == null || primitive.attributes == null)
                throw new InvalidDataException("没有可用 primitive");

            int posAccessor = primitive.attributes.POSITION;
            if (posAccessor < 0 || root.accessors == null || posAccessor >= root.accessors.Length)
                throw new InvalidDataException("缺少 POSITION");

            Vector3[] positions = ReadVec3(root, bin, posAccessor);
            if (positions == null || positions.Length == 0) throw new InvalidDataException("POSITION 为空");

            Vector2[] uvs = null;
            if (primitive.attributes.TEXCOORD_0 >= 0)
                uvs = ReadVec2(root, bin, primitive.attributes.TEXCOORD_0);
            if (uvs == null || uvs.Length < positions.Length) uvs = new Vector2[positions.Length];

            int[] triangles;
            if (primitive.indices >= 0) triangles = ReadIndices(root, bin, primitive.indices);
            else
            {
                triangles = new int[positions.Length];
                for (int i = 0; i < triangles.Length; i++) triangles[i] = i;
            }
            if (triangles == null || triangles.Length < 3) throw new InvalidDataException("索引为空");
            if (triangles.Length % 3 != 0)
            {
                int usable = triangles.Length - (triangles.Length % 3);
                Array.Resize(ref triangles, usable);
            }

            // glTF 为右手坐标系，Unity 为左手：翻转 X 并反转三角形绕序，否则模型会里外颠倒。
            const float flip = -1f;
            for (int i = 0; i < positions.Length; i++) positions[i].x *= flip;
            for (int i = 0; i + 2 < triangles.Length; i += 3)
            {
                int tmp = triangles[i + 1];
                triangles[i + 1] = triangles[i + 2];
                triangles[i + 2] = tmp;
            }

            var mesh = new Mesh();
            mesh.name = Path.GetFileNameWithoutExtension(fileName);
            if (positions.Length > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.vertices = positions;
            mesh.uv = uvs;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();

            Texture2D texture = null;
            if (root.images != null && root.images.Length > 0 && root.images[0] != null && root.images[0].bufferView >= 0)
                texture = DecodeTexture(root, bin, root.images[0].bufferView);

            float roughness = 0.9f;
            Color baseColor = Color.white;
            if (root.materials != null && root.materials.Length > 0 && root.materials[0] != null
                && root.materials[0].pbrMetallicRoughness != null)
            {
                GlbPbr pbr = root.materials[0].pbrMetallicRoughness;
                roughness = Mathf.Clamp01(pbr.roughnessFactor <= 0f ? 0.9f : pbr.roughnessFactor);
                if (pbr.baseColorFactor != null && pbr.baseColorFactor.Length >= 4)
                    baseColor = new Color(pbr.baseColorFactor[0], pbr.baseColorFactor[1], pbr.baseColorFactor[2], pbr.baseColorFactor[3]);
            }

            Material material = CreateMaterial(texture, baseColor, roughness, mesh);
            var model = new GlbModel
            {
                sourceFile = fileName,
                mesh = mesh,
                material = material,
                texture = texture,
                bounds = mesh.bounds,
                creasedEdges = EdgeExtractor.ExtractCreasedEdges(mesh, 28f),
            };
            return model;
        }

        private static Material CreateMaterial(Texture2D texture, Color baseColor, float roughness, Mesh mesh)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            var material = new Material(shader) { name = "GlbMaterial" };
            if (texture != null)
            {
                material.SetTexture("_BaseMap", texture);
                material.SetTexture("_MainTex", texture);
            }
            material.SetColor("_BaseColor", baseColor);
            material.SetColor("_Color", baseColor);
            material.SetFloat("_Smoothness", Mathf.Clamp01(1f - roughness));
            material.SetFloat("_Glossiness", Mathf.Clamp01(1f - roughness));
            material.SetFloat("_Metallic", 0f);
            if (material.HasProperty("_Cull")) material.SetFloat("_Cull", 2f);
            // 近景细节层：按模型 UV 密度平铺高频颗粒，弥补 12 m 尺度下只有 38 texel/m 的不足。
            // 关掉 DetailSurface.Enabled（或 PlayerPrefs epicspark.detail=0）即可完全回到改造前行为。
            DetailSurface.Apply(material, mesh);
            return material;
        }

        private static Texture2D DecodeTexture(GlbRoot root, byte[] bin, int bufferViewIndex)
        {
            try
            {
                if (bin == null || root.bufferViews == null || bufferViewIndex >= root.bufferViews.Length) return null;
                GlbBufferView view = root.bufferViews[bufferViewIndex];
                if (view == null || view.byteOffset + view.byteLength > bin.Length) return null;
                var png = new byte[view.byteLength];
                Buffer.BlockCopy(bin, view.byteOffset, png, 0, view.byteLength);
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true);
                if (!texture.LoadImage(png))
                {
                    UnityEngine.Object.Destroy(texture);
                    return null;
                }
                texture.name = "GlbTexture";
                texture.wrapMode = TextureWrapMode.Clamp;
                texture.filterMode = FilterMode.Trilinear;
                texture.anisoLevel = 4;
                return texture;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[GLB] 纹理解码失败: " + e.Message);
                return null;
            }
        }

        private static Vector3[] ReadVec3(GlbRoot root, byte[] bin, int accessorIndex)
        {
            GlbAccessor accessor = root.accessors[accessorIndex];
            float[] raw = ReadFloats(root, bin, accessor, 3);
            if (raw == null) return null;
            var result = new Vector3[accessor.count];
            for (int i = 0; i < result.Length; i++)
                result[i] = new Vector3(raw[i * 3], raw[i * 3 + 1], raw[i * 3 + 2]);
            return result;
        }

        private static Vector2[] ReadVec2(GlbRoot root, byte[] bin, int accessorIndex)
        {
            GlbAccessor accessor = root.accessors[accessorIndex];
            float[] raw = ReadFloats(root, bin, accessor, 2);
            if (raw == null) return null;
            var result = new Vector2[accessor.count];
            for (int i = 0; i < result.Length; i++)
                result[i] = new Vector2(raw[i * 2], raw[i * 2 + 1]);
            return result;
        }

        private static float[] ReadFloats(GlbRoot root, byte[] bin, GlbAccessor accessor, int components)
        {
            if (accessor == null || bin == null) return null;
            if (accessor.bufferView < 0 || root.bufferViews == null || accessor.bufferView >= root.bufferViews.Length) return null;
            GlbBufferView view = root.bufferViews[accessor.bufferView];
            int start = view.byteOffset + accessor.byteOffset;
            int needed = accessor.count * components * 4;
            if (start < 0 || start + needed > bin.Length) return null;
            var result = new float[accessor.count * components];
            for (int i = 0; i < result.Length; i++)
                result[i] = BitConverter.ToSingle(bin, start + i * 4);
            return result;
        }

        private static int[] ReadIndices(GlbRoot root, byte[] bin, int accessorIndex)
        {
            GlbAccessor accessor = root.accessors[accessorIndex];
            if (accessor == null || bin == null) return null;
            if (accessor.bufferView < 0 || root.bufferViews == null || accessor.bufferView >= root.bufferViews.Length) return null;
            GlbBufferView view = root.bufferViews[accessor.bufferView];
            int start = view.byteOffset + accessor.byteOffset;
            var result = new int[accessor.count];
            int size = accessor.componentType == 5125 ? 4 : accessor.componentType == 5123 ? 2 : 1;
            if (start < 0 || start + accessor.count * size > bin.Length) return null;
            for (int i = 0; i < result.Length; i++)
            {
                if (size == 4) result[i] = (int)BitConverter.ToUInt32(bin, start + i * 4);
                else if (size == 2) result[i] = BitConverter.ToUInt16(bin, start + i * 2);
                else result[i] = bin[start + i];
            }
            return result;
        }

        // ─────────────────────────── DTO ───────────────────────────

        [Serializable] private class GlbRoot
        {
            public GlbAssetInfo asset;
            public int scene;
            public GlbScene[] scenes;
            public GlbNode[] nodes;
            public GlbMesh[] meshes;
            public GlbAccessor[] accessors;
            public GlbBufferView[] bufferViews;
            public GlbImage[] images;
            public GlbTexture[] textures;
            public GlbMaterial[] materials;
        }

        [Serializable] private class GlbAssetInfo { public string version; public string generator; }
        [Serializable] private class GlbScene { public int[] nodes; }
        [Serializable] private class GlbNode { public string name; public int mesh = -1; public int[] children; }
        [Serializable] private class GlbMesh { public string name; public GlbPrimitive[] primitives; }
        [Serializable] private class GlbPrimitive { public GlbAttributes attributes; public int indices = -1; public int mode = 4; public int material = -1; }
        [Serializable] private class GlbAttributes { public int POSITION = -1; public int TEXCOORD_0 = -1; public int NORMAL = -1; }
        [Serializable] private class GlbAccessor
        {
            public int bufferView = -1;
            public int byteOffset;
            public int componentType;
            public string type;
            public int count;
            public float[] min;
            public float[] max;
        }
        [Serializable] private class GlbBufferView { public int buffer; public int byteOffset; public int byteLength; }
        [Serializable] private class GlbImage { public string uri; public int bufferView = -1; public string mimeType; }
        [Serializable] private class GlbTexture { public int source = -1; }
        [Serializable] private class GlbMaterial { public GlbPbr pbrMetallicRoughness; public bool doubleSided; }
        [Serializable] private class GlbPbr
        {
            public float[] baseColorFactor;
            public GlbTextureRef baseColorTexture;
            public float roughnessFactor = 1f;
            public float metallicFactor;
        }
        [Serializable] private class GlbTextureRef { public int index; }
    }
}
