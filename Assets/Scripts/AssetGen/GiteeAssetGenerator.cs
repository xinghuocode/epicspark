// 资产生成 Agent：搭建 Agent 把「一个语义槽的描述」交进来，返回一条可用的 ModelEntry。
// 设计文档链路：z-image-turbo 文生图 → RMBG-2.0 抠图 → Hunyuan3D-2 图生 3D → 写运行时模型库。
// 关键约束：
//   · baseUrl / apiKey 未配置时绝不发请求，走确定性兜底：从现有模型库按 category + 关键词挑最接近的一条克隆；
//   · 远端任何一步失败都回退到确定性兜底，绝不抛异常（返回 null 只代表模型库为空、真的无法克隆）；
//   · 同一次会话内同样的 (prompt, category) 直接复用已生成条目，避免重复刷请求 / 无限增长。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using EpicSpark.Build;
using EpicSpark.Core;
using UnityEngine;

namespace EpicSpark.AssetGen
{
    /// <summary>IAssetGenerator 实现（ai.gitee.com 的 z-image-turbo / RMBG-2.0 / Hunyuan3D-2）。</summary>
    public class GiteeAssetGenerator : IAssetGenerator
    {
        private readonly IModelCatalog modelCatalog;
        private readonly AssetGenSettings settings;
        private readonly RuntimeLibraryWriter libraryWriter;
        private readonly AssetGenPipeline pipeline;
        private readonly Dictionary<string, ModelEntry> sessionCache = new Dictionary<string, ModelEntry>(StringComparer.Ordinal);

        private ModelCatalog ownedCatalog;
        private static int sequence;

        public GiteeAssetGenerator()
            : this(null, null, null)
        {
        }

        /// <summary>
        /// modelCatalog 为空时用 Services.Catalog（再为空则自建 ModelCatalog）；
        /// settings 为空时读 persistentDataPath/EpicSpark/assetgen_config.json（缺省 = 离线兜底）；
        /// libraryWriter 为空时自建。
        /// </summary>
        public GiteeAssetGenerator(IModelCatalog catalog, AssetGenSettings settings, RuntimeLibraryWriter libraryWriter)
        {
            modelCatalog = catalog;
            this.settings = settings != null ? settings : AssetGenSettings.LoadOrCreate();
            this.libraryWriter = libraryWriter != null ? libraryWriter : new RuntimeLibraryWriter();
            pipeline = new AssetGenPipeline(this.settings);
            Debug.Log("[资产生成] 初始化：" + this.settings.Describe());
        }

        public AssetGenSettings Settings
        {
            get { return settings; }
        }

        public AssetGenPipeline Pipeline
        {
            get { return pipeline; }
        }

        /// <summary>远端成功生成并登记的新资产数量。</summary>
        public int GeneratedCount { get; private set; }

        /// <summary>确定性兜底（克隆现有资产）次数。</summary>
        public int FallbackCount { get; private set; }

        /// <summary>会话内复用已生成条目的次数。</summary>
        public int ReuseCount { get; private set; }

        public async Task<ModelEntry> GenerateAsync(string prompt, string category, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            string safePrompt = string.IsNullOrEmpty(prompt) ? "木制道具，陈旧，粗糙木纹" : prompt.Trim();
            string safeCategory = string.IsNullOrEmpty(category) ? "prop" : category.Trim().ToLowerInvariant();
            string cacheKey = safeCategory + "|" + safePrompt.ToLowerInvariant();

            ModelEntry cached;
            if (sessionCache.TryGetValue(cacheKey, out cached))
            {
                ReuseCount++;
                return cached;
            }

            if (settings.HasRemote)
            {
                AssetGenResult result = null;
                try
                {
                    result = await pipeline.RunAsync(safePrompt, safeCategory, ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[资产生成] 远端链路异常，转为确定性兜底: " + e.Message);
                    result = null;
                }

                if (result != null && result.success)
                {
                    ModelEntry remoteEntry = await BuildRemoteEntryAsync(safePrompt, safeCategory, result, ct);
                    if (remoteEntry != null)
                    {
                        Register(remoteEntry);
                        GeneratedCount++;
                        sessionCache[cacheKey] = remoteEntry;
                        return remoteEntry;
                    }
                    Debug.LogWarning("[资产生成] 远端成功但条目构建失败，回退确定性兜底。");
                }
                else if (result != null)
                {
                    Debug.LogWarning("[资产生成] 远端链路失败（stage=" + result.stage + "）：" + result.error + "，回退确定性兜底。");
                }
            }
            else
            {
                Debug.Log("[资产生成] 未配置 baseUrl/apiKey，走确定性兜底：克隆模型库中最接近的现有资产。");
            }

            ModelEntry fallback = await GenerateFallbackAsync(safePrompt, safeCategory, ct);
            if (fallback != null)
            {
                FallbackCount++;
                // 兜底克隆同样写入会话缓存：同一槽位语义不重复克隆（否则每轮都会新建条目）。
                sessionCache[cacheKey] = fallback;
            }
            return fallback;
        }

        // ─────────────────────────── 远端条目 ───────────────────────────

        private async Task<ModelEntry> BuildRemoteEntryAsync(string prompt, string category, AssetGenResult result, CancellationToken ct)
        {
            if (result == null || string.IsNullOrEmpty(result.glbPath)) return null;

            float[] vector = null;
            ILlmService llm = Services.Llm;
            if (llm != null && llm.HasRemote)
            {
                try
                {
                    vector = await llm.EmbedAsync(prompt, ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[资产生成] 远端 Embedding 失败，使用本地确定性向量: " + e.Message);
                    vector = null;
                }
            }
            if (!LocalEmbedding.IsUsableVector(vector, ModelCatalog.VectorDim))
                vector = LocalEmbedding.Embed(prompt + " " + category, ModelCatalog.VectorDim);
            else
                LocalEmbedding.NormalizeVector(vector);

            var entry = new ModelEntry
            {
                id = NextId(),
                name = BuildDisplayName(prompt),
                prompt = prompt,
                category = category,
                pivot = "bottom",
                glb = result.glbPath,
                vector = vector,
            };
            Debug.Log("[资产生成] 远端资产就绪: " + entry.id + " | " + entry.glb);
            return entry;
        }

        // ─────────────────────────── 确定性兜底 ───────────────────────────

        private async Task<ModelEntry> GenerateFallbackAsync(string prompt, string category, CancellationToken ct)
        {
            IModelCatalog catalog = ResolveCatalog();
            try
            {
                await catalog.LoadAsync(ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[资产生成] 兜底时加载模型库失败: " + e.Message);
            }

            IReadOnlyList<ModelEntry> all = await catalog.AllAsync();
            if (all == null || all.Count == 0)
            {
                Debug.LogWarning("[资产生成] 模型库为空，无法克隆兜底资产，返回 null 由搭建 Agent 记 warning。");
                return null;
            }

            ModelEntry best = null;
            float bestScore = -1f;
            for (int i = 0; i < all.Count; i++)
            {
                ModelEntry candidate = all[i];
                if (candidate == null) continue;
                float score = ScoreCandidate(prompt, category, candidate);
                if (best == null || score > bestScore)
                {
                    best = candidate;
                    bestScore = score;
                }
            }
            if (best == null) return null;

            // 直接克隆：向量沿用源资产（与库中 Qwen 向量同空间，后续检索仍然可比），glb 复用源模型。
            var clone = new ModelEntry
            {
                id = NextId(),
                name = (string.IsNullOrEmpty(best.name) ? best.id : best.name) + "（生成）",
                prompt = best.prompt,
                category = string.IsNullOrEmpty(best.category) ? category : best.category,
                pivot = string.IsNullOrEmpty(best.pivot) ? "bottom" : best.pivot,
                glb = best.glb,
                vector = CopyVector(best.vector),
            };
            Register(clone);
            Debug.Log(string.Format(CultureInfo.InvariantCulture,
                "[资产生成] 兜底克隆 {0} → {1}（类别 {2}，打分 {3:0.00}，glb 复用）",
                best.id, clone.id, clone.category, bestScore));
            return clone;
        }

        /// <summary>兜底选型打分：类别完全一致优先，其次关键词重合度。</summary>
        private static float ScoreCandidate(string prompt, string category, ModelEntry candidate)
        {
            float score = 0f;
            string candidateCategory = (candidate.category ?? string.Empty).Trim();
            if (candidateCategory.Length > 0 && string.Equals(candidateCategory, category, StringComparison.OrdinalIgnoreCase))
                score += 3f;
            else if (candidateCategory.Length > 0 && candidateCategory.IndexOf(category, StringComparison.OrdinalIgnoreCase) >= 0)
                score += 0.6f;

            score += LocalEmbedding.OverlapScore(prompt, (candidate.name ?? string.Empty) + " " + (candidate.prompt ?? string.Empty)) * 5f;

            // 语义提示里出现「建筑/房屋」类词时，建筑类资产略加权，避免兜底把建筑选成木桶
            if (category == "building" && candidateCategory == "building") score += 0.5f;
            return score;
        }

        // ─────────────────────────── 登记 ───────────────────────────

        private void Register(ModelEntry entry)
        {
            if (entry == null) return;
            try
            {
                if (libraryWriter != null) libraryWriter.Append(entry);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[资产生成] 写运行时模型库失败（本次仍可使用）: " + e.Message);
            }

            try
            {
                IModelCatalog catalog = ResolveCatalog();
                ModelCatalog concrete = catalog as ModelCatalog;
                if (concrete != null) concrete.RegisterRuntimeEntry(entry);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[资产生成] 热注册失败（本次仍可使用）: " + e.Message);
            }
        }

        private IModelCatalog ResolveCatalog()
        {
            if (modelCatalog != null) return modelCatalog;
            if (Services.Catalog != null) return Services.Catalog;
            if (ownedCatalog == null)
            {
                ownedCatalog = new ModelCatalog();
                Debug.Log("[资产生成] 未装配 Services.Catalog，资产生成 Agent 自建只读模型库（离线兜底）。");
            }
            return ownedCatalog;
        }

        // ─────────────────────────── 工具 ───────────────────────────

        private static string NextId()
        {
            int index = Interlocked.Increment(ref sequence);
            return "gen_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture)
                + "_" + index.ToString(CultureInfo.InvariantCulture);
        }

        private static string BuildDisplayName(string prompt)
        {
            string text = (prompt ?? string.Empty).Trim();
            if (text.Length == 0) return "生成资产";
            int comma = text.IndexOf('，');
            if (comma > 0) text = text.Substring(0, comma);
            if (text.Length > 18) text = text.Substring(0, 18) + "…";
            return text + "（生成）";
        }

        private static float[] CopyVector(float[] source)
        {
            if (source == null) return null;
            var copy = new float[source.Length];
            Array.Copy(source, copy, source.Length);
            return copy;
        }
    }
}
