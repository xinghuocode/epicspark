using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace EpicSpark.AI.Knowledge
{
    [Serializable]
    internal class KnowledgeEmbeddingRequest
    {
        public string model;
        public string[] input;
        public string encoding_format = "float";
    }

    [Serializable]
    internal class KnowledgeEmbeddingResponse
    {
        public KnowledgeEmbeddingData[] data;
    }

    [Serializable]
    internal class KnowledgeEmbeddingData
    {
        public int index;
        public float[] embedding;
    }

    [Serializable]
    internal class KnowledgeRerankStringRequest
    {
        public string model;
        public string query;
        public string[] documents;
        public int top_n;
        public bool return_documents;
    }

    [Serializable]
    internal class KnowledgeRerankObjectRequest
    {
        public string model;
        public string query;
        public KnowledgeRerankDocument[] documents;
        public int top_n;
        public bool return_documents;
    }

    [Serializable]
    internal class KnowledgeRerankDocument
    {
        public string text;
    }

    [Serializable]
    internal class KnowledgeRerankResponse
    {
        public KnowledgeRerankResult[] results;
        public KnowledgeRerankResult[] data;
        public float[] scores;
    }

    [Serializable]
    internal class KnowledgeRerankResult
    {
        public int index;
        public float relevance_score;
        public float score;
    }

    internal static class KnowledgeHttp
    {
        public static async Task<string> PostJsonAsync(
            string url,
            string apiKey,
            string json,
            int timeoutSeconds,
            CancellationToken cancellationToken)
        {
            using (var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
            {
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
                request.downloadHandler = new DownloadHandlerBuffer();
                request.timeout = Math.Max(1, timeoutSeconds);
                request.SetRequestHeader("Content-Type", "application/json");
                if (!string.IsNullOrEmpty(apiKey))
                    request.SetRequestHeader("Authorization", "Bearer " + apiKey);

                UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        request.Abort();
                        throw new TaskCanceledException("模型请求已取消");
                    }
                    await Task.Yield();
                }

                string responseText = request.downloadHandler != null ? request.downloadHandler.text : string.Empty;
                if (request.result != UnityWebRequest.Result.Success)
                {
                    int status = (int)request.responseCode;
                    string detail = responseText;
                    if (detail != null && detail.Length > 300) detail = detail.Substring(0, 300) + "…";
                    throw new KnowledgeProviderException(status, "HTTP " + status + " " + request.error + " | " + detail);
                }
                return responseText;
            }
        }

        public static bool IsRetryable(KnowledgeProviderException exception)
        {
            return exception.StatusCode == 0 || exception.StatusCode == 429 || exception.StatusCode >= 500;
        }
    }

    /// <summary>OpenAI 兼容 /embeddings 客户端，baseUrl 可在 KnowledgeSettings 中指定。</summary>
    internal sealed class KnowledgeRemoteEmbeddings : IKnowledgeEmbeddingProvider
    {
        public string Kind => "openai-compatible";
        public string Model => options.embedModel;
        public int Dim { get; private set; }
        public string DegradedReason { get; set; } = string.Empty;

        private readonly KnowledgeOptions options;
        private readonly string baseUrl;

        public KnowledgeRemoteEmbeddings(KnowledgeOptions options)
        {
            this.options = options;
            baseUrl = (options.baseUrl ?? string.Empty).TrimEnd('/');
        }

        public async Task<float[][]> EmbedAsync(IList<string> texts, CancellationToken cancellationToken)
        {
            // OpenAI 模式允许无 Key：LM Studio 等本地端点通常不需要鉴权。
            if (string.IsNullOrEmpty(baseUrl))
                throw new KnowledgeProviderException(0, "未配置 baseUrl");

            var output = new List<float[]>(texts.Count);
            int batchSize = Math.Max(1, options.embeddingBatchSize);
            for (int start = 0; start < texts.Count; start += batchSize)
            {
                int count = Math.Min(batchSize, texts.Count - start);
                var batch = new string[count];
                for (int i = 0; i < count; i++) batch[i] = texts[start + i];
                float[][] vectors = await EmbedBatchWithRetry(batch, cancellationToken);
                output.AddRange(vectors);
            }
            return output.ToArray();
        }

        private async Task<float[][]> EmbedBatchWithRetry(string[] batch, CancellationToken cancellationToken)
        {
            KnowledgeProviderException last = null;
            int attempts = Math.Max(1, options.remoteAttempts);
            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                try
                {
                    string json = JsonUtility.ToJson(new KnowledgeEmbeddingRequest
                    {
                        model = options.embedModel,
                        input = batch,
                    });
                    string response = await KnowledgeHttp.PostJsonAsync(
                        baseUrl + "/embeddings",
                        options.apiKey,
                        json,
                        options.timeoutSeconds,
                        cancellationToken);
                    var parsed = JsonUtility.FromJson<KnowledgeEmbeddingResponse>(response);
                    if (parsed == null || parsed.data == null || parsed.data.Length != batch.Length)
                        throw new KnowledgeProviderException(0, "嵌入响应条数不匹配或 data 为空");

                    var vectors = new float[batch.Length][];
                    for (int i = 0; i < parsed.data.Length; i++)
                    {
                        KnowledgeEmbeddingData item = parsed.data[i];
                        int index = item.index >= 0 && item.index < batch.Length ? item.index : i;
                        if (item.embedding == null || item.embedding.Length == 0)
                            throw new KnowledgeProviderException(0, "嵌入响应包含空向量");
                        if (Dim == 0) Dim = item.embedding.Length;
                        if (item.embedding.Length != Dim)
                            throw new KnowledgeProviderException(0, "嵌入模型返回了不同维度");
                        vectors[index] = KnowledgeText.NormalizeVector(item.embedding);
                    }
                    return vectors;
                }
                catch (KnowledgeProviderException exception)
                {
                    last = exception;
                    if (!KnowledgeHttp.IsRetryable(exception) || attempt == attempts) throw;
                    await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), cancellationToken);
                }
            }
            throw last ?? new KnowledgeProviderException(0, "嵌入请求失败");
        }
    }

    /// <summary>Qwen3-Reranker 兼容客户端；自动探测 /rerank 与 /rerankings。</summary>
    internal sealed class KnowledgeRemoteReranker : IKnowledgeReranker
    {
        public string Kind => "openai-compatible-rerank";
        public string Model => options.rerankModel;
        public string DegradedReason { get; set; } = string.Empty;

        private readonly KnowledgeOptions options;
        private readonly string baseUrl;
        private string workingPath;

        public KnowledgeRemoteReranker(KnowledgeOptions options)
        {
            this.options = options;
            baseUrl = (options.baseUrl ?? string.Empty).TrimEnd('/');
        }

        public async Task<float[]> ScoreAsync(
            string query,
            IList<string> documents,
            CancellationToken cancellationToken)
        {
            if (documents.Count == 0) return new float[0];
            if (string.IsNullOrEmpty(baseUrl))
                throw new KnowledgeProviderException(0, "未配置 baseUrl");

            string[] paths = string.IsNullOrEmpty(workingPath)
                ? new[] { "/rerank", "/rerankings" }
                : new[] { workingPath };
            KnowledgeProviderException last = null;
            foreach (string path in paths)
            {
                for (int format = 0; format < 2; format++)
                {
                    try
                    {
                        string json = format == 0
                            ? JsonUtility.ToJson(new KnowledgeRerankStringRequest
                            {
                                model = options.rerankModel,
                                query = query,
                                documents = ToArray(documents),
                                top_n = documents.Count,
                                return_documents = false,
                            })
                            : JsonUtility.ToJson(new KnowledgeRerankObjectRequest
                            {
                                model = options.rerankModel,
                                query = query,
                                documents = ToDocuments(documents),
                                top_n = documents.Count,
                                return_documents = false,
                            });
                        string response = await KnowledgeHttp.PostJsonAsync(
                            baseUrl + path,
                            options.apiKey,
                            json,
                            options.timeoutSeconds,
                            cancellationToken);
                        float[] scores = ParseScores(response, documents.Count);
                        workingPath = path;
                        return scores;
                    }
                    catch (KnowledgeProviderException exception)
                    {
                        last = exception;
                        if (exception.StatusCode == 404) break; // 尝试下一路径
                        if (exception.StatusCode == 400 || exception.StatusCode == 422) continue; // 尝试另一 documents 格式
                        if (!KnowledgeHttp.IsRetryable(exception)) throw;
                        await Task.Delay(500, cancellationToken);
                    }
                }
            }
            throw last ?? new KnowledgeProviderException(0, "重排请求失败");
        }

        private static string[] ToArray(IList<string> documents)
        {
            var array = new string[documents.Count];
            for (int i = 0; i < documents.Count; i++) array[i] = documents[i];
            return array;
        }

        private static KnowledgeRerankDocument[] ToDocuments(IList<string> documents)
        {
            var array = new KnowledgeRerankDocument[documents.Count];
            for (int i = 0; i < documents.Count; i++) array[i] = new KnowledgeRerankDocument { text = documents[i] };
            return array;
        }

        private static float[] ParseScores(string response, int count)
        {
            var parsed = JsonUtility.FromJson<KnowledgeRerankResponse>(response);
            if (parsed == null) throw new KnowledgeProviderException(0, "重排响应不是合法 JSON");
            var scores = new float[count];
            var assigned = new bool[count];
            KnowledgeRerankResult[] items = parsed.results != null && parsed.results.Length > 0
                ? parsed.results
                : parsed.data;

            if (items != null)
            {
                for (int position = 0; position < items.Length; position++)
                {
                    KnowledgeRerankResult item = items[position];
                    int index = item.index >= 0 && item.index < count ? item.index : position;
                    if (index >= count) continue;
                    scores[index] = Math.Abs(item.relevance_score) > 0.000001f
                        ? item.relevance_score
                        : item.score;
                    assigned[index] = true;
                }
            }
            else if (parsed.scores != null)
            {
                for (int i = 0; i < count && i < parsed.scores.Length; i++)
                {
                    scores[i] = parsed.scores[i];
                    assigned[i] = true;
                }
            }

            int assignedCount = 0;
            foreach (bool value in assigned) if (value) assignedCount++;
            if (assignedCount == 0) throw new KnowledgeProviderException(0, "重排响应没有可用分数");

            float minimum = float.MaxValue;
            float maximum = float.MinValue;
            for (int i = 0; i < count; i++)
            {
                if (!assigned[i]) continue;
                minimum = Math.Min(minimum, scores[i]);
                maximum = Math.Max(maximum, scores[i]);
            }
            if (maximum - minimum > 0.000001f && (minimum < 0f || maximum > 1f))
                for (int i = 0; i < count; i++) scores[i] = (scores[i] - minimum) / (maximum - minimum);
            return scores;
        }
    }
}
