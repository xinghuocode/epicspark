using System;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace EpicSpark.AI.Knowledge
{
    /// <summary>
    /// 知识库检索客户端（纯 UnityWebRequest，零外部依赖）。
    /// 负责与 Tools/knowledge_rag 暴露的 RAG 服务通信：
    /// <list type="bullet">
    /// <item>开局：<see cref="GetEventCardAsync"/> 把用户输入的历史事件变成可塞进提示词的知识上下文</item>
    /// <item>过程中：<see cref="SearchAsync"/> 按任意说法做混合检索 + 重排</item>
    /// <item>运维：<see cref="HealthAsync"/> 检查服务与降级状态</item>
    /// </list>
    /// 线程约定与 <c>OpenAIClient</c> 一致：await 期间通过 Task.Yield 回到主线程，可直接调用 Unity API。
    /// </summary>
    public class KnowledgeClient : IDisposable
    {
        readonly string baseUrl;
        readonly string adminToken;
        readonly int timeoutSeconds;
        readonly int defaultTopK;
        readonly int budgetChars;

        public KnowledgeClient(KnowledgeSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            baseUrl = (settings.baseUrl ?? "").TrimEnd('/');
            adminToken = settings.adminToken ?? string.Empty;
            timeoutSeconds = Mathf.Max(1, settings.timeoutSeconds);
            defaultTopK = Mathf.Max(1, settings.defaultTopK);
            budgetChars = Mathf.Max(200, settings.contextBudgetChars);
        }

        public KnowledgeClient(string baseUrl, int timeoutSeconds = 15, int defaultTopK = 6, int contextBudgetChars = 2400)
        {
            this.baseUrl = (baseUrl ?? "").TrimEnd('/');
            this.timeoutSeconds = timeoutSeconds;
            this.defaultTopK = defaultTopK;
            this.budgetChars = contextBudgetChars;
            adminToken = string.Empty;
        }

        /// <summary>服务健康检查。degraded 非空说明远端模型不可用、已降级到本地向量/重排。</summary>
        public async Task<KnowledgeHealth> HealthAsync(System.Threading.CancellationToken cancellationToken = default)
        {
            using (var req = NewGet(baseUrl + "/health"))
            {
                await SendAsync(req, cancellationToken);
                return Parse<KnowledgeHealth>(req.downloadHandler.text);
            }
        }

        /// <summary>
        /// 事件卡片：用户输入"法国大革命"/"山顶洞人"等说法，返回事件元数据、证据块与提示词上下文。
        /// 未命中已收录事件时 matched/event 为 null，但仍会走通用检索返回相近知识（见 evidences/context）。
        /// </summary>
        public async Task<KnowledgeEventCard> GetEventCardAsync(
            string name,
            int topK = 0,
            int budgetCharsOverride = 0,
            System.Threading.CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name 不能为空", nameof(name));
            var payload = new KnowledgeEventRequest
            {
                name = name.Trim(),
                top_k = topK > 0 ? topK : defaultTopK,
                budget_chars = budgetCharsOverride > 0 ? budgetCharsOverride : budgetChars,
            };
            using (var req = NewJsonPost(baseUrl + "/v1/knowledge/event", JsonUtility.ToJson(payload)))
            {
                await SendAsync(req, cancellationToken);
                return Parse<KnowledgeEventCard>(req.downloadHandler.text);
            }
        }

        /// <summary>通用检索：混合召回 + 重排，返回带打分的文本块与拼好的上下文。</summary>
        public async Task<KnowledgeSearchResponse> SearchAsync(
            string query,
            int topK = 0,
            bool useRerank = true,
            System.Threading.CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("query 不能为空", nameof(query));
            var payload = new KnowledgeSearchRequest
            {
                query = query.Trim(),
                top_k = topK > 0 ? topK : defaultTopK,
                use_rerank = useRerank,
            };
            using (var req = NewJsonPost(baseUrl + "/v1/knowledge/search", JsonUtility.ToJson(payload)))
            {
                await SendAsync(req, cancellationToken);
                return Parse<KnowledgeSearchResponse>(req.downloadHandler.text);
            }
        }

        // ================== 底层封装 ==================

        UnityWebRequest NewJsonPost(string url, string json)
        {
            var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST);
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            req.downloadHandler = new DownloadHandlerBuffer();
            ApplyCommonHeaders(req);
            return req;
        }

        UnityWebRequest NewGet(string url)
        {
            var req = UnityWebRequest.Get(url);
            ApplyCommonHeaders(req);
            return req;
        }

        void ApplyCommonHeaders(UnityWebRequest req)
        {
            req.timeout = timeoutSeconds;
            req.SetRequestHeader("Content-Type", "application/json");
            if (!string.IsNullOrEmpty(adminToken))
                req.SetRequestHeader("X-RAG-Token", adminToken);
        }

        async Task SendAsync(UnityWebRequest req, System.Threading.CancellationToken cancellationToken)
        {
            var op = req.SendWebRequest();
            while (!op.isDone)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    req.Abort();
                    throw new TaskCanceledException("知识库请求已取消");
                }
                await Task.Yield();
            }

            if (req.result != UnityWebRequest.Result.Success)
                throw new KnowledgeApiException((int)req.responseCode, req.error, req.downloadHandler?.text);
        }

        static T Parse<T>(string text) where T : class
        {
            T value;
            try
            {
                value = JsonUtility.FromJson<T>(text);
            }
            catch (Exception exc)
            {
                throw new KnowledgeApiException(0, "响应解析失败: " + exc.Message, Shorten(text));
            }
            return value ?? throw new KnowledgeApiException(0, "响应为空", Shorten(text));
        }

        static string Shorten(string s) => string.IsNullOrEmpty(s) || s.Length <= 300 ? s : s.Substring(0, 300) + "…";

        public void Dispose()
        {
            // 请求对象在 SendAsync 内即时释放，这里保留以对齐 OpenAIClient 的 IDisposable 用法。
        }
    }

    /// <summary>知识库服务返回非 2xx 或响应无法解析时抛出。</summary>
    public class KnowledgeApiException : Exception
    {
        public int StatusCode { get; }
        public string ErrorBody { get; }

        public KnowledgeApiException(int statusCode, string message, string errorBody)
            : base($"知识库服务错误 {statusCode}: {message} | {Shorten(errorBody)}")
        {
            StatusCode = statusCode;
            ErrorBody = errorBody;
        }

        static string Shorten(string s) => string.IsNullOrEmpty(s) ? "" : (s.Length > 300 ? s.Substring(0, 300) + "…" : s);
    }
}
