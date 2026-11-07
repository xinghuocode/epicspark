using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace EpicSpark.AI.Knowledge
{
    internal interface IKnowledgeEmbeddingProvider
    {
        string Kind { get; }
        string Model { get; }
        int Dim { get; }
        string DegradedReason { get; }
        Task<float[][]> EmbedAsync(IList<string> texts, CancellationToken cancellationToken);
    }

    internal interface IKnowledgeReranker
    {
        string Kind { get; }
        string Model { get; }
        string DegradedReason { get; }
        Task<float[]> ScoreAsync(string query, IList<string> documents, CancellationToken cancellationToken);
    }

    /// <summary>完全在 Unity 进程内运行的确定性向量提供方。</summary>
    internal sealed class KnowledgeHashingEmbeddings : IKnowledgeEmbeddingProvider
    {
        public string Kind => "local-hashing";
        public string Model => "local-hashing-" + dimension;
        public int Dim => dimension;
        public string DegradedReason { get; set; } = string.Empty;

        private readonly int dimension;

        public KnowledgeHashingEmbeddings(int dimension)
        {
            if (dimension <= 0) throw new ArgumentOutOfRangeException(nameof(dimension));
            this.dimension = dimension;
        }

        public async Task<float[][]> EmbedAsync(IList<string> texts, CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                var vectors = new float[texts.Count][];
                for (int i = 0; i < texts.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    vectors[i] = KnowledgeText.HashingEmbed(texts[i], dimension);
                }
                return vectors;
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Unity 内本地词汇重排：覆盖率、Jaccard、词项密度与短语命中。</summary>
    internal sealed class KnowledgeLocalReranker : IKnowledgeReranker
    {
        public string Kind => "local-lexical";
        public string Model => "local-lexical-reranker";
        public string DegradedReason { get; set; } = string.Empty;

        public Task<float[]> ScoreAsync(string query, IList<string> documents, CancellationToken cancellationToken)
        {
            string normalizedQuery = KnowledgeText.Normalize(query);
            var queryTokens = KnowledgeText.TokenSet(normalizedQuery);
            var scores = new float[documents.Count];
            for (int i = 0; i < documents.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string document = KnowledgeText.Normalize(documents[i]);
                var documentTokens = KnowledgeText.TokenSet(document);
                float coverage = KnowledgeText.Coverage(queryTokens, documentTokens);
                float jaccard = KnowledgeText.Jaccard(queryTokens, documentTokens);
                int overlap = 0;
                foreach (string token in queryTokens)
                    if (documentTokens.Contains(token)) overlap++;
                float density = overlap / ((float)Math.Sqrt(documentTokens.Count) + 1f);
                float phrase = normalizedQuery.Length > 0 && document.Contains(normalizedQuery) ? 1f : 0f;
                float score = 0.50f * coverage + 0.25f * jaccard + 0.15f * Math.Min(1f, density) + 0.10f * phrase;
                scores[i] = Math.Max(0f, Math.Min(1f, score));
            }
            return Task.FromResult(scores);
        }
    }

    /// <summary>远端模型调用失败；引擎会捕获它并切换到 Unity 内实现。</summary>
    public class KnowledgeProviderException : Exception
    {
        public int StatusCode { get; }

        public KnowledgeProviderException(int statusCode, string message)
            : base(message)
        {
            StatusCode = statusCode;
        }
    }
}
