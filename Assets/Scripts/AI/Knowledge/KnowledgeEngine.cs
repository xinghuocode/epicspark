using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace EpicSpark.AI.Knowledge
{
    /// <summary>
    /// Unity 进程内 RAG 引擎：事件解析 -> 向量/BM25 双路召回 -> RRF -> 重排 -> 上下文。
    /// 不创建监听端口，也不依赖 Python 或外部检索进程。
    /// </summary>
    internal sealed class KnowledgeEngine
    {
        private static readonly Regex ChunkHeaderRegex = new Regex(
            @"^《([^》]+)》（([^）]*)）([^：]{1,12})：",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private readonly KnowledgeOptions options;
        private readonly string indexPath;
        private readonly IKnowledgeEmbeddingProvider remoteEmbeddings;
        private readonly IKnowledgeReranker remoteReranker;
        private readonly KnowledgeHashingEmbeddings localEmbeddings;
        private readonly KnowledgeLocalReranker localReranker;
        private readonly SemaphoreSlim initializationLock = new SemaphoreSlim(1, 1);

        private List<KnowledgeEventInfo> events;
        private List<KnowledgeChunk> chunks;
        private Dictionary<string, KnowledgeEventInfo> eventById;
        private Dictionary<string, KnowledgeChunk> chunkById;
        private KnowledgeBm25Index bm25;
        private KnowledgeVectorIndex index;
        private IKnowledgeEmbeddingProvider activeEmbeddings;
        private string fingerprint;
        private string embeddingDegraded = string.Empty;
        private string rerankDegraded = string.Empty;
        private bool initialized;

        public KnowledgeEngine(
            KnowledgeOptions options,
            string indexPath,
            IKnowledgeEmbeddingProvider remoteEmbeddings,
            IKnowledgeReranker remoteReranker)
        {
            this.options = options ?? throw new ArgumentNullException(nameof(options));
            this.indexPath = indexPath ?? throw new ArgumentNullException(nameof(indexPath));
            this.remoteEmbeddings = remoteEmbeddings;
            this.remoteReranker = remoteReranker;
            localEmbeddings = new KnowledgeHashingEmbeddings(options.localEmbedDim);
            localReranker = new KnowledgeLocalReranker();
        }

        public bool IsInitialized => initialized;
        public int EventCount => events?.Count ?? 0;
        public int ChunkCount => chunks?.Count ?? 0;
        public IReadOnlyList<KnowledgeEventInfo> Events => events ?? new List<KnowledgeEventInfo>();

        public async Task InitializeAsync(
            IList<KnowledgeEventInfo> sourceEvents,
            CancellationToken cancellationToken,
            bool forceRebuild = false)
        {
            await initializationLock.WaitAsync(cancellationToken);
            try
            {
                if (initialized && !forceRebuild) return;
                initialized = false;
                embeddingDegraded = string.Empty;
                rerankDegraded = string.Empty;

                events = sourceEvents != null
                    ? sourceEvents.Where(item => item != null && !string.IsNullOrEmpty(item.id)).ToList()
                    : new List<KnowledgeEventInfo>();
                if (events.Count == 0) throw new InvalidOperationException("知识库没有可用事件");

                eventById = events.ToDictionary(item => item.id, item => item, StringComparer.Ordinal);
                chunks = KnowledgeCorpus.BuildChunks(events);
                chunkById = chunks.ToDictionary(item => item.chunkId, item => item, StringComparer.Ordinal);
                fingerprint = KnowledgeCorpus.Fingerprint(chunks);
                bm25 = new KnowledgeBm25Index();
                foreach (KnowledgeChunk chunk in chunks) bm25.Add(chunk.chunkId, chunk.text);
                bm25.FinalizeIndex();

                bool wantsRemote = options.UseRemoteEmbeddings && remoteEmbeddings != null;
                IKnowledgeEmbeddingProvider preferred = wantsRemote ? remoteEmbeddings : localEmbeddings;
                index = forceRebuild ? null : KnowledgeVectorIndex.Load(indexPath);
                if (index != null && !index.IsCompatible(fingerprint, preferred.Kind, preferred.Model, PreferredDim(preferred)))
                    index = null;

                if (index != null)
                {
                    activeEmbeddings = preferred;
                }
                else
                {
                    try
                    {
                        await BuildIndexAsync(preferred, cancellationToken);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception exception) when (wantsRemote)
                    {
                        embeddingDegraded = "远端嵌入不可用，已改用 Unity 内向量: " + exception.Message;
                        KnowledgeVectorIndex localIndex = forceRebuild ? null : KnowledgeVectorIndex.Load(indexPath);
                        if (localIndex != null && localIndex.IsCompatible(
                                fingerprint,
                                localEmbeddings.Kind,
                                localEmbeddings.Model,
                                localEmbeddings.Dim))
                        {
                            index = localIndex;
                            activeEmbeddings = localEmbeddings;
                        }
                        else
                        {
                            await BuildIndexAsync(localEmbeddings, cancellationToken);
                        }
                    }
                }
                initialized = true;
            }
            finally
            {
                initializationLock.Release();
            }
        }

        private int PreferredDim(IKnowledgeEmbeddingProvider provider)
        {
            return provider == localEmbeddings ? localEmbeddings.Dim : provider.Dim;
        }

        private async Task BuildIndexAsync(
            IKnowledgeEmbeddingProvider provider,
            CancellationToken cancellationToken)
        {
            if (provider == remoteEmbeddings && provider.Dim == 0)
            {
                // 远端维度在第一次响应后才知道；加载索引时 dim=0 作为通配，
                // 新建索引则由真实响应确定。
            }
            var texts = chunks.Select(chunk => chunk.text).ToArray();
            float[][] vectors = await provider.EmbedAsync(texts, cancellationToken);
            if (vectors == null || vectors.Length != chunks.Count || vectors.Length == 0)
                throw new InvalidOperationException("向量数量与文本块数量不一致");
            int dim = vectors[0].Length;
            var vectorList = vectors.ToList();
            index = new KnowledgeVectorIndex(fingerprint, provider.Kind, provider.Model, dim, vectorList);
            index.Save(indexPath);
            activeEmbeddings = provider;
        }

        public KnowledgeMatch Resolve(string query)
        {
            EnsureReady();
            return KnowledgeCorpus.Resolve(events, query);
        }

        public KnowledgeHealth GetHealth()
        {
            bool ready = initialized && index != null;
            string degraded = JoinDegraded();
            return new KnowledgeHealth
            {
                status = string.IsNullOrEmpty(degraded) ? "ok" : "degraded",
                service = "Unity in-process C# RAG",
                version = "2.0.0",
                events = EventCount,
                chunks = ChunkCount,
                index_ready = ready,
                embedding = new KnowledgeEmbeddingInfo
                {
                    kind = index != null ? index.EmbedKind : (options.UseRemoteEmbeddings ? "openai-compatible" : localEmbeddings.Kind),
                    model = index != null ? index.EmbedModel : activeEmbeddings?.Model ?? localEmbeddings.Model,
                    dim = index?.Dim ?? 0,
                    degraded = !string.IsNullOrEmpty(embeddingDegraded),
                    degraded_reason = embeddingDegraded ?? string.Empty,
                },
                reranker = new KnowledgeRerankerInfo
                {
                    kind = options.UseRemoteRerank && remoteReranker != null ? remoteReranker.Kind : localReranker.Kind,
                    model = options.UseRemoteRerank && remoteReranker != null ? remoteReranker.Model : localReranker.Model,
                    degraded = !string.IsNullOrEmpty(rerankDegraded),
                    degraded_reason = rerankDegraded ?? string.Empty,
                },
                server_time = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"),
            };
        }

        public async Task<KnowledgeSearchResponse> SearchAsync(
            string query,
            int topK,
            bool useRerank,
            KnowledgeFilter filter,
            CancellationToken cancellationToken)
        {
            EnsureReady();
            var stopwatch = Stopwatch.StartNew();
            if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("query 不能为空", nameof(query));
            query = query.Trim();
            topK = Math.Max(1, Math.Min(topK > 0 ? topK : options.defaultTopK, 50));
            int candidateLimit = Math.Max(topK, Math.Min(Math.Max(1, options.candidates), 200));

            KnowledgeMatch resolved = Resolve(query);
            bool expand = resolved != null && resolved.confidence >= 0.6f;
            string retrievalQuery = expand ? query + " " + resolved.title : query;

            List<KnowledgeScoredPosition> denseHits = new List<KnowledgeScoredPosition>();
            try
            {
                float[][] queryVectors = await activeEmbeddings.EmbedAsync(
                    new[] { retrievalQuery },
                    cancellationToken);
                denseHits = index.Search(queryVectors[0], candidateLimit * 2);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                // 远端索引与本地向量维度不同，不能混用；退化为 BM25 单路召回仍可用。
                embeddingDegraded = "查询向量化失败，本次使用 BM25 召回: " + exception.Message;
            }

            List<KnowledgeScoredId> sparseHits = bm25.Search(retrievalQuery, candidateLimit * 2);
            var denseScores = new Dictionary<string, float>(StringComparer.Ordinal);
            var sparseScores = new Dictionary<string, float>(StringComparer.Ordinal);
            var fused = new Dictionary<string, float>(StringComparer.Ordinal);
            int rrfK = Math.Max(1, options.rrfK);

            for (int rank = 0; rank < denseHits.Count; rank++)
            {
                KnowledgeChunk chunk = chunks[denseHits[rank].position];
                if (!PassesFilter(chunk, filter)) continue;
                denseScores[chunk.chunkId] = denseHits[rank].score;
                AddFused(fused, chunk.chunkId, 1f / (rrfK + rank + 1f));
            }
            for (int rank = 0; rank < sparseHits.Count; rank++)
            {
                KnowledgeScoredId hit = sparseHits[rank];
                if (!chunkById.TryGetValue(hit.id, out KnowledgeChunk chunk) || !PassesFilter(chunk, filter)) continue;
                sparseScores[hit.id] = hit.score;
                AddFused(fused, hit.id, 1f / (rrfK + rank + 1f));
            }

            if (fused.Count == 0)
            {
                stopwatch.Stop();
                return EmptySearch(query, resolved, stopwatch.ElapsedMilliseconds);
            }

            List<string> orderedIds = fused.Keys.OrderByDescending(id => fused[id]).Take(candidateLimit).ToList();
            int headCount = Math.Min(Math.Max(1, options.rerankTopN), orderedIds.Count);
            List<string> head = orderedIds.Take(headCount).ToList();
            var rerankScores = new Dictionary<string, float>(StringComparer.Ordinal);
            bool rerankUsed = false;
            string rerankProvider = "none";

            if (useRerank)
            {
                IKnowledgeReranker provider = options.UseRemoteRerank && remoteReranker != null
                    ? remoteReranker
                    : localReranker;
                var documents = head.Select(id => chunkById[id].text).ToArray();
                try
                {
                    float[] scores = await provider.ScoreAsync(retrievalQuery, documents, cancellationToken);
                    if (scores.Length != documents.Length) throw new InvalidOperationException("重排分数数量不匹配");
                    for (int i = 0; i < scores.Length; i++) rerankScores[head[i]] = scores[i];
                    rerankUsed = true;
                    rerankProvider = provider.Kind;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception exception) when (provider != localReranker)
                {
                    rerankDegraded = "远端重排不可用，已改用 Unity 内重排: " + exception.Message;
                    float[] scores = await localReranker.ScoreAsync(retrievalQuery, documents, cancellationToken);
                    for (int i = 0; i < scores.Length; i++) rerankScores[head[i]] = scores[i];
                    rerankUsed = true;
                    rerankProvider = localReranker.Kind;
                }
            }

            if (rerankUsed) orderedIds = head;
            float minFused = fused.Values.Min();
            float maxFused = fused.Values.Max();
            string resolvedId = resolved != null && resolved.confidence >= 0.9f ? resolved.event_id : null;
            var hits = new List<KnowledgeHit>(orderedIds.Count);
            foreach (string id in orderedIds)
            {
                KnowledgeChunk chunk = chunkById[id];
                float normalizedFused = Normalize(fused[id], minFused, maxFused);
                bool hasRerank = rerankScores.TryGetValue(id, out float rerank);
                float final = hasRerank ? 0.75f * rerank + 0.25f * normalizedFused : normalizedFused;
                if (!string.IsNullOrEmpty(resolvedId) && chunk.eventId == resolvedId) final += 0.05f;
                hits.Add(new KnowledgeHit
                {
                    chunk = chunk,
                    dense = denseScores.TryGetValue(id, out float dense) ? dense : 0f,
                    sparse = sparseScores.TryGetValue(id, out float sparse) ? sparse : 0f,
                    fused = fused[id],
                    rerank = rerank,
                    hasRerank = hasRerank,
                    final = final,
                });
            }

            hits.Sort((left, right) => right.final.CompareTo(left.final));
            if (hits.Count > 0 && options.relativeCutoff > 0)
            {
                float threshold = hits[0].final * options.relativeCutoff;
                hits = hits.Where(hit => hit.final >= threshold).ToList();
            }
            List<KnowledgeHit> selected = Diversify(hits, topK);
            var items = new List<KnowledgeItem>(selected.Count);
            for (int i = 0; i < selected.Count; i++) items.Add(selected[i].ToItem(i + 1));

            stopwatch.Stop();
            return new KnowledgeSearchResponse
            {
                query = query,
                took_ms = (float)stopwatch.Elapsed.TotalMilliseconds,
                resolved = resolved,
                items = items.ToArray(),
                total_candidates = fused.Count,
                rerank_used = rerankUsed,
                rerank_provider = rerankProvider,
                degraded_reason = JoinDegraded(),
                context = BuildContext(items),
            };
        }

        public async Task<KnowledgeEventCard> GetEventCardAsync(
            string name,
            int topK,
            int budgetChars,
            CancellationToken cancellationToken)
        {
            EnsureReady();
            var stopwatch = Stopwatch.StartNew();
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name 不能为空", nameof(name));
            name = name.Trim();
            KnowledgeMatch match = Resolve(name);
            if (match == null || !eventById.TryGetValue(match.event_id, out KnowledgeEventInfo item))
            {
                KnowledgeSearchResponse fallback = await SearchAsync(
                    name,
                    topK,
                    true,
                    null,
                    cancellationToken);
                stopwatch.Stop();
                return new KnowledgeEventCard
                {
                    query = name,
                    took_ms = (float)stopwatch.Elapsed.TotalMilliseconds,
                    matched = null,
                    @event = null,
                    items = fallback.items,
                    context = fallback.context,
                };
            }

            topK = Math.Max(1, Math.Min(topK > 0 ? topK : options.defaultTopK, 20));
            List<KnowledgeChunk> eventChunks = chunks.Where(chunk => chunk.eventId == item.id).ToList();
            var documents = eventChunks.Select(chunk => chunk.text).ToArray();
            IKnowledgeReranker provider = options.UseRemoteRerank && remoteReranker != null
                ? remoteReranker
                : localReranker;
            float[] scores;
            try
            {
                scores = await provider.ScoreAsync(name, documents, cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) when (provider != localReranker)
            {
                rerankDegraded = "事件内远端重排失败，已改用 Unity 内重排: " + exception.Message;
                scores = await localReranker.ScoreAsync(name, documents, cancellationToken);
            }

            var evidences = new List<KnowledgeEvidence>(eventChunks.Count);
            for (int i = 0; i < eventChunks.Count; i++)
            {
                evidences.Add(new KnowledgeEvidence
                {
                    chunk_id = eventChunks[i].chunkId,
                    section = eventChunks[i].section,
                    section_label = eventChunks[i].sectionLabel,
                    text = eventChunks[i].text,
                    score = i < scores.Length ? scores[i] : 0f,
                });
            }
            evidences = evidences.OrderByDescending(evidence => evidence.score).Take(topK).ToList();
            string context = BuildEvidenceContext(evidences, budgetChars);
            stopwatch.Stop();
            return new KnowledgeEventCard
            {
                query = name,
                took_ms = (float)stopwatch.Elapsed.TotalMilliseconds,
                matched = match,
                @event = item,
                evidences = evidences.ToArray(),
                items = new KnowledgeItem[0],
                context = context,
            };
        }

        public string BuildContext(IList<KnowledgeItem> items, int budgetChars = 0)
        {
            int budget = budgetChars > 0 ? budgetChars : options.contextBudgetChars;
            var lines = new List<string>();
            int used = 0;
            foreach (KnowledgeItem item in items)
            {
                string line = ContextLine(item.title, item.period, item.section_label, item.text);
                if (lines.Count == 0 && line.Length > budget)
                    line = line.Substring(0, Math.Max(0, budget - 1)) + "…";
                if (lines.Count > 0 && used + line.Length > budget) break;
                lines.Add(line);
                used += line.Length + 1;
            }
            return string.Join("\n", lines);
        }

        private string BuildEvidenceContext(IList<KnowledgeEvidence> items, int budgetChars)
        {
            int budget = budgetChars > 0 ? budgetChars : options.contextBudgetChars;
            var lines = new List<string>();
            int used = 0;
            foreach (KnowledgeEvidence item in items)
            {
                string line = ContextLine(string.Empty, string.Empty, item.section_label, item.text);
                if (lines.Count == 0 && line.Length > budget)
                    line = line.Substring(0, Math.Max(0, budget - 1)) + "…";
                if (lines.Count > 0 && used + line.Length > budget) break;
                lines.Add(line);
                used += line.Length + 1;
            }
            return string.Join("\n", lines);
        }

        private static string ContextLine(string title, string period, string section, string text)
        {
            Match match = ChunkHeaderRegex.Match(text ?? string.Empty);
            if (match.Success)
            {
                string chunkTitle = match.Groups[1].Value;
                string metadata = match.Groups[2].Value;
                string chunkSection = match.Groups[3].Value;
                string chunkPeriod = metadata.Contains("｜") ? metadata.Split('｜')[0] : metadata;
                return "【" + chunkTitle + "｜" + chunkPeriod + "｜" + chunkSection + "】"
                    + text.Substring(match.Length);
            }
            return "【" + title + "｜" + period + "｜" + section + "】" + text;
        }

        private List<KnowledgeHit> Diversify(IList<KnowledgeHit> hits, int topK)
        {
            int limit = Math.Max(1, options.maxChunksPerEvent);
            var perEvent = new Dictionary<string, int>(StringComparer.Ordinal);
            var selected = new List<KnowledgeHit>(topK);
            foreach (KnowledgeHit hit in hits)
            {
                perEvent.TryGetValue(hit.chunk.eventId, out int count);
                if (count >= limit) continue;
                perEvent[hit.chunk.eventId] = count + 1;
                selected.Add(hit);
                if (selected.Count >= topK) break;
            }
            if (selected.Count == 0) selected.AddRange(hits.Take(topK));
            return selected;
        }

        private static bool PassesFilter(KnowledgeChunk chunk, KnowledgeFilter filter)
        {
            if (filter == null || filter.IsEmpty) return true;
            if (!string.IsNullOrEmpty(filter.era) && chunk.era != filter.era) return false;
            if (!string.IsNullOrEmpty(filter.region) &&
                (chunk.region == null || !chunk.region.Contains(filter.region))) return false;
            if (!string.IsNullOrEmpty(filter.event_id) && chunk.eventId != filter.event_id) return false;
            if (filter.year.HasValue && (chunk.startYear > filter.year.Value || chunk.endYear < filter.year.Value)) return false;
            if (filter.year_from.HasValue && chunk.endYear < filter.year_from.Value) return false;
            if (filter.year_to.HasValue && chunk.startYear > filter.year_to.Value) return false;
            return true;
        }

        private static void AddFused(Dictionary<string, float> fused, string id, float score)
        {
            fused.TryGetValue(id, out float current);
            fused[id] = current + score;
        }

        private static float Normalize(float score, float minimum, float maximum)
        {
            return maximum - minimum <= 0.0000001f ? 1f : (score - minimum) / (maximum - minimum);
        }

        private KnowledgeSearchResponse EmptySearch(string query, KnowledgeMatch resolved, long elapsedMs)
        {
            return new KnowledgeSearchResponse
            {
                query = query,
                took_ms = elapsedMs,
                resolved = resolved,
                items = new KnowledgeItem[0],
                context = string.Empty,
                total_candidates = 0,
                rerank_used = false,
                rerank_provider = "none",
                degraded_reason = JoinDegraded(),
            };
        }

        private string JoinDegraded()
        {
            if (!string.IsNullOrEmpty(embeddingDegraded) && !string.IsNullOrEmpty(rerankDegraded))
                return embeddingDegraded + "；" + rerankDegraded;
            return embeddingDegraded + rerankDegraded;
        }

        private void EnsureReady()
        {
            if (!initialized || index == null || activeEmbeddings == null)
                throw new InvalidOperationException("知识库尚未初始化，请先调用 InitializeAsync");
        }
    }
}
