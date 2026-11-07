using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace EpicSpark.AI.Knowledge
{
    /// <summary>
    /// Unity 进程内知识库客户端。名称保留为 Client 以兼容主程序既有调用，
    /// 但它不会连接任何独立检索服务：语料、BM25、向量索引与重排均在当前 Unity 进程内。
    /// baseUrl 仅用于可选的 OpenAI 兼容 embedding / rerank 模型端点。
    /// </summary>
    public class KnowledgeClient : IDisposable
    {
        private readonly KnowledgeSettings settings;
        private readonly KnowledgeOptions options;
        private readonly string resourcePath;
        private readonly string indexPath;
        private readonly KnowledgeEngine engine;
        private readonly SemaphoreSlim clientLock = new SemaphoreSlim(1, 1);
        private bool initialized;
        private bool disposed;

        public KnowledgeClient(KnowledgeSettings settings)
        {
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
            options = settings.ToOptions();
            resourcePath = settings.resourcePath;
            indexPath = Path.Combine(
                Application.persistentDataPath,
                "Knowledge",
                string.IsNullOrEmpty(settings.indexFileName) ? "vectors.bin" : settings.indexFileName);
            engine = new KnowledgeEngine(
                options,
                indexPath,
                new KnowledgeRemoteEmbeddings(options),
                new KnowledgeRemoteReranker(options));
        }

        /// <summary>使用默认 Resources 语料，并允许直接指定模型 baseUrl。</summary>
        public KnowledgeClient(
            string baseUrl,
            int timeoutSeconds = 30,
            int defaultTopK = 6,
            int contextBudgetChars = 2400)
        {
            options = new KnowledgeOptions
            {
                baseUrl = (baseUrl ?? string.Empty).TrimEnd('/'),
                timeoutSeconds = timeoutSeconds,
                defaultTopK = defaultTopK,
                contextBudgetChars = contextBudgetChars,
            };
            resourcePath = "Knowledge/Events";
            indexPath = Path.Combine(Application.persistentDataPath, "Knowledge", "vectors.bin");
            engine = new KnowledgeEngine(
                options,
                indexPath,
                new KnowledgeRemoteEmbeddings(options),
                new KnowledgeRemoteReranker(options));
        }

        public bool IsInitialized => initialized;
        public IReadOnlyList<KnowledgeEventInfo> Events => engine.Events;

        public async Task InitializeAsync(CancellationToken cancellationToken = default, bool forceRebuild = false)
        {
            ThrowIfDisposed();
            await clientLock.WaitAsync(cancellationToken);
            try
            {
                if (initialized && !forceRebuild) return;
                List<KnowledgeEventInfo> events = KnowledgeUnityLoader.LoadEvents(resourcePath);
                await engine.InitializeAsync(events, cancellationToken, forceRebuild);
                initialized = true;
            }
            finally
            {
                clientLock.Release();
            }
        }

        public async Task<KnowledgeHealth> HealthAsync(CancellationToken cancellationToken = default)
        {
            await EnsureInitialized(cancellationToken);
            return engine.GetHealth();
        }

        public async Task<KnowledgeEventCard> GetEventCardAsync(
            string name,
            int topK = 0,
            int budgetCharsOverride = 0,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name 不能为空", nameof(name));
            await EnsureInitialized(cancellationToken);
            return await engine.GetEventCardAsync(name, topK, budgetCharsOverride, cancellationToken);
        }

        public async Task<KnowledgeSearchResponse> SearchAsync(
            string query,
            int topK = 0,
            bool useRerank = true,
            CancellationToken cancellationToken = default)
        {
            return await SearchAsync(query, null, topK, useRerank, cancellationToken);
        }

        public async Task<KnowledgeSearchResponse> SearchAsync(
            string query,
            KnowledgeFilter filter,
            int topK = 0,
            bool useRerank = true,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("query 不能为空", nameof(query));
            await EnsureInitialized(cancellationToken);
            return await engine.SearchAsync(query, topK, useRerank, filter, cancellationToken);
        }

        public async Task<KnowledgeMatch> ResolveAsync(string query, CancellationToken cancellationToken = default)
        {
            await EnsureInitialized(cancellationToken);
            return engine.Resolve(query);
        }

        public async Task<KnowledgeHealth> RebuildIndexAsync(CancellationToken cancellationToken = default)
        {
            await InitializeAsync(cancellationToken, true);
            return engine.GetHealth();
        }

        private async Task EnsureInitialized(CancellationToken cancellationToken)
        {
            if (!initialized) await InitializeAsync(cancellationToken);
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(KnowledgeClient));
        }

        public void Dispose()
        {
            disposed = true;
            clientLock.Dispose();
        }
    }

    /// <summary>保留旧异常类型，避免主程序既有 catch 代码失效。</summary>
    public class KnowledgeApiException : Exception
    {
        public int StatusCode { get; }
        public string ErrorBody { get; }

        public KnowledgeApiException(int statusCode, string message, string errorBody)
            : base(message)
        {
            StatusCode = statusCode;
            ErrorBody = errorBody;
        }
    }
}
