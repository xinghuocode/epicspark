using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace EpicSpark.AI.OpenAI
{
    /// <summary>
    /// OpenAI 兼容端点客户端(纯 UnityWebRequest, 无外部依赖)。
    /// 支持 /v1/chat/completions (非流式 + SSE 流式)、/v1/models、/v1/embeddings。
    /// </summary>
    public class OpenAIClient : IDisposable
    {
        readonly string baseUrl;
        readonly string apiKey;
        readonly int timeoutSeconds;
        readonly string defaultModel;
        readonly List<UnityWebRequest> activeRequests = new List<UnityWebRequest>();

        public OpenAIClient(OpenAISettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            baseUrl = settings.baseUrl.TrimEnd('/');
            apiKey = settings.apiKey ?? string.Empty;
            timeoutSeconds = settings.timeoutSeconds;
            defaultModel = settings.model;
        }

        public OpenAIClient(string baseUrl, string apiKey, string defaultModel, int timeoutSeconds = 120)
        {
            this.baseUrl = (baseUrl ?? "").TrimEnd('/');
            this.apiKey = apiKey ?? string.Empty;
            this.defaultModel = defaultModel;
            this.timeoutSeconds = timeoutSeconds;
        }

        // ================== Chat Completions ==================

        /// <summary>非流式聊天补全。网络/解析/协议错误会以异常形式抛出, 在服务端返回 4xx/5xx 时抛出 <see cref="OpenAIApiException"/>。</summary>
        public async Task<ChatResponse> ChatAsync(
            IReadOnlyList<ChatMessage> messages,
            string model = null,
            ChatOptions options = null,
            System.Threading.CancellationToken cancellationToken = default)
        {
            if (messages == null || messages.Count == 0) throw new ArgumentException("messages 不能为空", nameof(messages));
            options = options ?? new ChatOptions();
            if (options.stream) throw new ArgumentException("请改用 ChatStreamAsync", nameof(options));

            string body = ChatRequestJson.Build(model ?? defaultModel, messages, options);
            using (var req = NewJsonPost(baseUrl + "/chat/completions", body))
            {
                await SendAsync(req, cancellationToken);
                return JsonUtility.FromJson<ChatResponse>(req.downloadHandler.text)
                       ?? throw new InvalidOperationException("响应解析失败: " + Shorten(req.downloadHandler.text));
            }
        }

        /// <summary>
        /// 流式聊天补全(SSE)。每收到一个内容增量回调 onDelta; 首个增量会携带 role, 最后一个增量前 finishReason 为 null, 结束时 finishReason 非空。
        /// 取消: cancellationToken 或 CancellationToken.None。
        /// </summary>
        public async Task ChatStreamAsync(
            IReadOnlyList<ChatMessage> messages,
            Action<string, string, string> onDelta, // (deltaContent, role, finishReason)
            string model = null,
            ChatOptions options = null,
            System.Threading.CancellationToken cancellationToken = default)
        {
            if (messages == null || messages.Count == 0) throw new ArgumentException("messages 不能为空", nameof(messages));
            if (onDelta == null) throw new ArgumentNullException(nameof(onDelta));
            options = options ?? new ChatOptions();
            options.stream = true;

            string body = ChatRequestJson.Build(model ?? defaultModel, messages, options);
            using (var req = NewJsonPost(baseUrl + "/chat/completions", body))
            {
                req.downloadHandler = new StreamingDownloadHandler(delegate (string sseEvent)
                {
                    if (string.IsNullOrEmpty(sseEvent)) return;
                    if (sseEvent == "[DONE]") { onDelta(null, null, "stop"); return; }
                    var chunk = JsonUtility.FromJson<ChatStreamChunk>(sseEvent);
                    if (chunk?.choices == null || chunk.choices.Length == 0) return;
                    var choice = chunk.choices[0];
                    onDelta(choice.delta?.content, choice.delta?.role, choice.finish_reason);
                });

                await SendAsync(req, cancellationToken);
            }
        }

        // ================== Models ==================

        public async Task<ModelListResponse> ListModelsAsync(System.Threading.CancellationToken cancellationToken = default)
        {
            using (var req = NewGet(baseUrl + "/models"))
            {
                await SendAsync(req, cancellationToken);
                return JsonUtility.FromJson<ModelListResponse>(req.downloadHandler.text)
                       ?? throw new InvalidOperationException("响应解析失败: " + Shorten(req.downloadHandler.text));
            }
        }

        // ================== Embeddings ==================

        /// <summary>文本向量化。注意: 需要端点加载了 embedding 模型(如 ai.gitee.com 的 Qwen3-Embedding-8B); LM Studio 的 chat 模型不支持此接口。</summary>
        public async Task<EmbeddingResponse> CreateEmbeddingAsync(string input, string model = null, System.Threading.CancellationToken cancellationToken = default)
        {
            var payload = new EmbeddingRequest { model = model ?? defaultModel, input = input };
            string body = JsonUtility.ToJson(payload);
            using (var req = NewJsonPost(baseUrl + "/embeddings", body))
            {
                await SendAsync(req, cancellationToken);
                return JsonUtility.FromJson<EmbeddingResponse>(req.downloadHandler.text)
                       ?? throw new InvalidOperationException("响应解析失败: " + Shorten(req.downloadHandler.text));
            }
        }

        // ================== 底层封装 ==================

        UnityWebRequest NewJsonPost(string url, string json)
        {
            var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            req.uploadHandler = new UploadHandlerRaw(bytes);
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
            if (!string.IsNullOrEmpty(apiKey))
                req.SetRequestHeader("Authorization", "Bearer " + apiKey);
            activeRequests.Add(req);
        }

        async Task SendAsync(UnityWebRequest req, System.Threading.CancellationToken cancellationToken)
        {
            try
            {
                var op = req.SendWebRequest();
                while (!op.isDone)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        req.Abort();
                        throw new TaskCanceledException("请求已取消");
                    }
                    await Task.Yield();
                }

                if (req.result != UnityWebRequest.Result.Success)
                    throw new OpenAIApiException((int)req.responseCode, req.error, req.downloadHandler?.text);

                long code = req.responseCode;
                if (code == 401 || code == 403 || code == 404 || code == 422 || code >= 500)
                    throw new OpenAIApiException((int)code, "HTTP " + code, req.downloadHandler?.text);
            }
            finally
            {
                activeRequests.Remove(req);
            }
        }

        static string Shorten(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length > 200 ? s.Substring(0, 200) + "…" : s;
        }

        public void Dispose()
        {
            foreach (var req in activeRequests) req.Abort();
            activeRequests.Clear();
        }
    }

    /// <summary>服务端返回错误状态码时抛出, ErrorBody 为原始响应体(通常是 JSON 格式的 error 对象)。</summary>
    public class OpenAIApiException : Exception
    {
        public int StatusCode { get; }
        public string ErrorBody { get; }

        public OpenAIApiException(int statusCode, string message, string errorBody)
            : base($"OpenAI API 错误 {statusCode}: {message} | {Shorten(errorBody)}")
        {
            StatusCode = statusCode;
            ErrorBody = errorBody;
        }

        static string Shorten(string s) => string.IsNullOrEmpty(s) ? "" : (s.Length > 300 ? s.Substring(0, 300) + "…" : s);
    }
}
