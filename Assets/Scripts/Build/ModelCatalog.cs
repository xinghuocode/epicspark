// 缓存模型库检索：读取 StreamingAssets/Models/model_library.json（43 条 Qwen3-Embedding-8B 归一化向量），
// 并合并 Application.persistentDataPath/EpicSpark/runtime_library.json 里由资产生成 Agent 追加的新条目。
// 检索路径：远端 Embedding（Services.Llm）→ 失败/未配置时用本地确定性向量（LocalEmbedding）→ 余弦相似度取最优。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EpicSpark.Core;
using UnityEngine;
using UnityEngine.Networking;

namespace EpicSpark.Build
{
    /// <summary>
    /// IModelCatalog 实现：向量检索 + 查询缓存 + 运行时库热注册。
    /// 编辑器 / Windows / 其它桌面平台用 File 读 StreamingAssets；Android / WebGL 用 UnityWebRequest 读。
    /// </summary>
    public class ModelCatalog : IModelCatalog
    {
        /// <summary>嵌入维度，与 model_library.json 的 meta.dim 一致。</summary>
        public const int VectorDim = LocalEmbedding.DefaultDim;

        /// <summary>主库在 StreamingAssets 下的相对路径。</summary>
        public const string LibraryRelativePath = "Models/model_library.json";

        /// <summary>运行时追加库文件名（与 RuntimeLibraryWriter 共用同一路径约定）。</summary>
        public const string RuntimeLibraryFileName = "runtime_library.json";

        /// <summary>缓存上限：超过后整体清空，避免长局内存膨胀。</summary>
        private const int CacheLimit = 1024;

        private readonly object gate = new object();
        private readonly List<ModelEntry> entries = new List<ModelEntry>();
        private readonly Dictionary<string, float[]> vectorCache = new Dictionary<string, float[]>(StringComparer.Ordinal);
        private readonly Dictionary<string, ModelMatch> matchCache = new Dictionary<string, ModelMatch>(StringComparer.Ordinal);
        private readonly string libraryPathOverride;

        private Task loadTask;

        public ModelCatalog()
        {
        }

        /// <summary>指定主库文件的绝对路径（编辑器测试 / 自定义部署用）。</summary>
        public ModelCatalog(string libraryPathOverride)
        {
            this.libraryPathOverride = libraryPathOverride;
        }

        public bool IsLoaded { get; private set; }

        /// <summary>打印每次检索的命中与分数（排障用，PlayerPrefs 键 EpicSpark.VerboseMatching=1 打开）。</summary>
        public static bool VerboseMatching;

        /// <summary>最近一次加载/检索的失败原因，便于 Bootstrap 展示。</summary>
        public string LastError { get; private set; } = string.Empty;

        public int Count
        {
            get { lock (gate) { return entries.Count; } }
        }

        public int QueryVectorCacheCount
        {
            get { lock (gate) { return vectorCache.Count; } }
        }

        /// <summary>运行时库文件路径：Application.persistentDataPath/EpicSpark/runtime_library.json。</summary>
        public static string RuntimeLibraryPath
        {
            get
            {
                return Path.Combine(Application.persistentDataPath, "EpicSpark", RuntimeLibraryFileName);
            }
        }

        // ─────────────────────────── 加载 ───────────────────────────

        public Task LoadAsync(CancellationToken ct)
        {
            if (IsLoaded) return Task.CompletedTask;
            lock (gate)
            {
                if (loadTask == null) loadTask = LoadInternalAsync(ct);
                return loadTask;
            }
        }

        private async Task LoadInternalAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            string json = null;
            string source = "StreamingAssets/" + LibraryRelativePath;
            try
            {
                if (!string.IsNullOrEmpty(libraryPathOverride))
                {
                    source = libraryPathOverride;
                    json = ReadLocalText(libraryPathOverride);
                }
                else
                {
                    json = await ReadStreamingAssetsTextAsync(LibraryRelativePath, ct);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                LastError = "读取主模型库失败: " + e.Message;
                Debug.LogWarning("[模型库] " + LastError);
            }

            int added = 0;
            if (!string.IsNullOrEmpty(json)) added += ParseLibraryJson(json, false);
            if (added == 0) Debug.LogWarning("[模型库] 主库没有可用条目（" + source + "），检索将只能命中运行时新生成资产。");

            int runtimeAdded = LoadRuntimeLibrary();
            IsLoaded = true;

            if (entries.Count == 0)
            {
                LastError = "模型库为空";
                Debug.LogWarning("[模型库] 模型库为空，所有语义槽都将走资产生成兜底。");
            }
            else
            {
                Debug.Log(string.Format("[模型库] 已加载 {0} 条资产（主库 {1} + 运行时 {2}），维度 {3}。",
                    entries.Count, added, runtimeAdded, VectorDim));
            }
        }

        private int ParseLibraryJson(string json, bool runtime)
        {
            ModelLibrary library = null;
            try
            {
                library = JsonUtility.FromJson<ModelLibrary>(json);
            }
            catch (Exception e)
            {
                LastError = "模型库 JSON 解析失败: " + e.Message;
                Debug.LogWarning("[模型库] " + LastError);
                return 0;
            }
            if (library == null || library.entries == null) return 0;

            int added = 0;
            for (int i = 0; i < library.entries.Count; i++)
                if (TryAddEntry(library.entries[i], runtime)) added++;
            return added;
        }

        private int LoadRuntimeLibrary()
        {
            try
            {
                string path = RuntimeLibraryPath;
                if (!File.Exists(path)) return 0;
                string json = File.ReadAllText(path, Encoding.UTF8);
                if (string.IsNullOrEmpty(json)) return 0;
                return ParseLibraryJson(json, true);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[模型库] 读取运行时库失败，忽略: " + e.Message);
                return 0;
            }
        }

        /// <summary>运行时热注册一条新资产（资产生成 Agent 写入 runtime_library.json 后同步内存）。</summary>
        public void RegisterRuntimeEntry(ModelEntry entry)
        {
            if (entry == null) return;
            bool added = TryAddEntry(entry, true);
            if (added)
            {
                Debug.Log("[模型库] 已注册运行时资产: " + entry.id + " | " + entry.name + " | " + entry.category);
            }
            else
            {
                Debug.LogWarning("[模型库] 运行时资产注册被跳过（id 重复或内容为空）: " + entry.id);
            }
        }

        private bool TryAddEntry(ModelEntry entry, bool runtime)
        {
            if (entry == null) return false;
            if (string.IsNullOrEmpty(entry.id))
                entry.id = (runtime ? "gen_auto_" : "mdl_auto_") + (entries.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(entry.name) && string.IsNullOrEmpty(entry.prompt))
            {
                Debug.LogWarning("[模型库] 丢弃空条目: " + entry.id);
                return false;
            }
            if (string.IsNullOrEmpty(entry.name)) entry.name = entry.id;

            string normalizedText = LocalEmbedding.Normalize((entry.name ?? string.Empty) + " " + (entry.prompt ?? string.Empty));
            if (!LocalEmbedding.IsUsableVector(entry.vector, VectorDim))
            {
                // 向量缺失/维度不符：用本地确定性向量补齐，保证这条资产仍可被检索到。
                Debug.LogWarning("[模型库] 条目 " + entry.id + " 的向量不可用（" + LocalEmbedding.Fingerprint(entry.vector)
                    + "），改用本地向量。");
                entry.vector = LocalEmbedding.Embed(normalizedText, VectorDim);
            }
            else
            {
                LocalEmbedding.NormalizeVector(entry.vector);
            }

            lock (gate)
            {
                if (IndexOfIdUnsafe(entry.id) >= 0) return false;
                entries.Add(entry);
                matchCache.Clear();
            }
            return true;
        }

        private int IndexOfIdUnsafe(string id)
        {
            for (int i = 0; i < entries.Count; i++)
                if (entries[i] != null && entries[i].id == id) return i;
            return -1;
        }

        private static string ReadLocalText(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            return File.ReadAllText(path, Encoding.UTF8);
        }

        private async Task<string> ReadStreamingAssetsTextAsync(string relativePath, CancellationToken ct)
        {
            string url = Application.streamingAssetsPath + "/" + relativePath;
            bool needsWebRequest = Application.platform == RuntimePlatform.Android
                || Application.platform == RuntimePlatform.WebGLPlayer
                || url.IndexOf("://", StringComparison.Ordinal) >= 0;

            if (!needsWebRequest)
            {
                if (!File.Exists(url)) throw new FileNotFoundException("找不到模型库文件", url);
                return File.ReadAllText(url, Encoding.UTF8);
            }

            using (var request = UnityWebRequest.Get(url))
            {
                request.timeout = 30;
                UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    if (ct.IsCancellationRequested)
                    {
                        request.Abort();
                        throw new OperationCanceledException(ct);
                    }
                    await Task.Yield();
                }
                if (request.result != UnityWebRequest.Result.Success)
                    throw new IOException("HTTP " + (int)request.responseCode + " " + request.error);
                string text = request.downloadHandler != null ? request.downloadHandler.text : null;
                if (string.IsNullOrEmpty(text)) throw new IOException("模型库内容为空");
                return text;
            }
        }

        // ─────────────────────────── 查询 ───────────────────────────

        public Task<IReadOnlyList<ModelEntry>> AllAsync()
        {
            return Task.FromResult((IReadOnlyList<ModelEntry>)Snapshot());
        }

        /// <summary>同步快照（资产生成兜底选型用）。</summary>
        public IReadOnlyList<ModelEntry> Snapshot()
        {
            lock (gate) { return new List<ModelEntry>(entries); }
        }

        public ModelEntry FindById(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            lock (gate)
            {
                int index = IndexOfIdUnsafe(id);
                return index >= 0 ? entries[index] : null;
            }
        }

        /// <summary>
        /// 带类别偏好的检索：语义槽是"建筑"时不该匹配到"石阶"（放大后就是一堆乱石头）。
        /// 只要有符合类别的条目，就优先用类别正确的，哪怕相似度略低。
        /// </summary>
        public async Task<ModelMatch> FindBestAsync(string query, ICollection<string> preferredCategories, CancellationToken ct)
        {
            if (preferredCategories == null || preferredCategories.Count == 0)
                return await FindBestAsync(query, ct);

            string key = query == null ? string.Empty : query.Trim().ToLowerInvariant();
            if (key.Length == 0) return new ModelMatch { entry = null, similarity = 0f };

            if (!IsLoaded)
            {
                try { await LoadAsync(ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) { Debug.LogWarning("[模型库] 加载失败，按空库检索: " + e.Message); }
            }

            ct.ThrowIfCancellationRequested();
            float[] vector = await ResolveQueryVectorAsync(key, ct);

            lock (gate)
            {
                ModelEntry best = null;
                float bestScore = 0f;
                ModelEntry bestPreferred = null;
                float bestPreferredScore = 0f;
                for (int i = 0; i < entries.Count; i++)
                {
                    ModelEntry entry = entries[i];
                    if (entry == null || entry.vector == null || entry.vector.Length != VectorDim) continue;
                    float cosine = Mathf.Clamp01(LocalEmbedding.Cosine(vector, entry.vector));
                    float lexical = BilingualMatcher.Score(key, entry);
                    float score = Mathf.Max(cosine, lexical);
                    if (best == null || score > bestScore)
                    {
                        best = entry;
                        bestScore = score;
                    }
                    if (!MatchesCategory(entry, preferredCategories)) continue;
                    if (bestPreferred == null || score > bestPreferredScore)
                    {
                        bestPreferred = entry;
                        bestPreferredScore = score;
                    }
                }

                ModelEntry chosen = bestPreferred != null ? bestPreferred : best;
                float chosenScore = bestPreferred != null ? bestPreferredScore : bestScore;
                var match = new ModelMatch { entry = chosen, similarity = chosen == null ? 0f : chosenScore };
                if (VerboseMatching)
                {
                    Debug.Log("[模型库] \"" + key + "\" → " + (chosen != null ? chosen.id + "/" + chosen.category
                        + " 相似度=" + chosenScore.ToString("F3") : "无命中")
                        + (bestPreferred != null && bestPreferred != best ? "（按类别优先，整体最优是 " + best.id + "）" : ""));
                }
                return match;
            }
        }

        private static bool MatchesCategory(ModelEntry entry, ICollection<string> categories)
        {
            if (entry == null || string.IsNullOrEmpty(entry.category)) return false;
            foreach (string candidate in categories)
            {
                if (string.IsNullOrEmpty(candidate)) continue;
                if (string.Equals(entry.category, candidate, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public async Task<ModelMatch> FindBestAsync(string query, CancellationToken ct)
        {
            string key = query == null ? string.Empty : query.Trim().ToLowerInvariant();
            if (key.Length == 0) return new ModelMatch { entry = null, similarity = 0f };

            lock (gate)
            {
                ModelMatch cached;
                if (matchCache.TryGetValue(key, out cached)) return cached;
            }

            if (!IsLoaded)
            {
                try { await LoadAsync(ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) { Debug.LogWarning("[模型库] 加载失败，按空库检索: " + e.Message); }
            }

            ct.ThrowIfCancellationRequested();
            float[] vector = await ResolveQueryVectorAsync(key, ct);

            lock (gate)
            {
                ModelEntry best = null;
                float bestScore = 0f;
                for (int i = 0; i < entries.Count; i++)
                {
                    ModelEntry entry = entries[i];
                    if (entry == null || entry.vector == null || entry.vector.Length != VectorDim) continue;
                    float cosine = Mathf.Clamp01(LocalEmbedding.Cosine(vector, entry.vector));
                    // 美术库的 prompt 是英文、灰盒语义是中文：离线时纯向量几乎不命中，
                    // 因此用「双语概念重合分」作为下限，保证复用现成美术资产这一核心体验。
                    float lexical = BilingualMatcher.Score(key, entry);
                    float score = Mathf.Max(cosine, lexical);
                    if (best == null || score > bestScore)
                    {
                        best = entry;
                        bestScore = score;
                    }
                }
                var match = new ModelMatch { entry = best, similarity = best == null ? 0f : bestScore };
                if (VerboseMatching)
                {
                    Debug.Log("[模型库] \"" + key + "\" → "
                              + (best != null ? best.id + " 相似度=" + bestScore.ToString("F3") : "无命中"));
                }
                if (matchCache.Count >= CacheLimit) matchCache.Clear();
                matchCache[key] = match;
                return match;
            }
        }

        /// <summary>查询文本 → 向量。远端优先，失败/未配置/维度不符时回退本地确定性向量，并缓存结果。</summary>
        private async Task<float[]> ResolveQueryVectorAsync(string key, CancellationToken ct)
        {
            lock (gate)
            {
                float[] cached;
                if (vectorCache.TryGetValue(key, out cached) && LocalEmbedding.IsUsableVector(cached, VectorDim))
                    return cached;
            }

            float[] vector = null;
            ILlmService llm = Services.Llm;
            if (llm != null && llm.HasRemote)
            {
                try
                {
                    vector = await llm.EmbedAsync(key, ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[模型库] 远端 Embedding 失败，改用本地确定性向量: " + e.Message);
                    vector = null;
                }
            }
            else
            {
                Debug.Log("[模型库] 未配置远端 Embedding，使用本地确定性向量（SHA-256 特征哈希，1024 维）。");
            }

            if (LocalEmbedding.IsUsableVector(vector, VectorDim))
            {
                LocalEmbedding.NormalizeVector(vector);
            }
            else if (vector == null)
            {
                vector = LocalEmbedding.Embed(key, VectorDim);
            }
            else
            {
                Debug.LogWarning("[模型库] 远端向量维度不是 " + VectorDim + "，裁剪/补零后再比较。");
                vector = LocalEmbedding.FitDimension(vector, VectorDim);
            }

            lock (gate)
            {
                if (vectorCache.Count >= CacheLimit) vectorCache.Clear();
                vectorCache[key] = vector;
            }
            return vector;
        }

        /// <summary>清空查询缓存（换模型、换库后调用）。</summary>
        public void ClearCaches()
        {
            lock (gate)
            {
                vectorCache.Clear();
                matchCache.Clear();
            }
        }
    }
}
