using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
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

        // ================== 通用 HTTP (模型生成管线等) ==================

        /// <summary>POST JSON 到 baseUrl + path, 返回原始响应体。</summary>
        public async Task<string> PostJsonAsync(string path, string json, System.Threading.CancellationToken cancellationToken = default)
        {
            using (var req = NewJsonPost(baseUrl + path, json))
            {
                await SendAsync(req, cancellationToken);
                return req.downloadHandler.text;
            }
        }

        /// <summary>
        /// POST multipart/form-data 到 baseUrl + path, 返回响应体字节。
        /// 使用原始 TCP + SslStream 手写 HTTP/1.1 请求 —— 经过实测, UnityWebRequest 与 Mono HttpClient
        /// 在编辑器(Linux)上都会丢弃自定义 date/Date/x-bce-date 头, 而 gitee 3D 服务后端(百度 BOS)
        /// 强制要求 Date 头(否则 400 MissingDateHeader)。只有手写报文才能确保头按原样到达服务端。
        /// </summary>
        public async Task<byte[]> PostMultipartAsync(string path, Dictionary<string, string> fields, byte[] fileBytes, string fileFieldName, string fileName, string fileMime, System.Threading.CancellationToken cancellationToken = default)
        {
            var uri = new Uri(baseUrl + path);
            int port = uri.Port != -1 ? uri.Port : (uri.Scheme == "https" ? 443 : 80);

            // ---- 构造 multipart 主体 ----
            string boundary = "----EpicSpark" + Guid.NewGuid().ToString("N");
            var body = new MemoryStream();
            Action<string> writeText = s =>
            {
                byte[] b = Encoding.UTF8.GetBytes(s);
                body.Write(b, 0, b.Length);
            };
            foreach (var kv in fields)
            {
                writeText($"--{boundary}\r\nContent-Disposition: form-data; name=\"{kv.Key}\"\r\n\r\n{kv.Value}\r\n");
            }
            writeText($"--{boundary}\r\nContent-Disposition: form-data; name=\"{fileFieldName}\"; filename=\"{fileName}\"\r\nContent-Type: {fileMime}\r\n\r\n");
            body.Write(fileBytes, 0, fileBytes.Length);
            writeText($"\r\n--{boundary}--\r\n");
            byte[] bodyBytes = body.ToArray();

            // ---- 手写 HTTP 请求头(完全自控, 任何 HTTP 库都无法再丢弃 date 头) ----
            var head = new StringBuilder(512);
            head.Append($"POST {uri.PathAndQuery} HTTP/1.1\r\n");
            head.Append($"Host: {uri.Host}\r\n");
            // BOS 强制要求日期头。实测: 小写 date 在传输中被中间层(WAF/代理)剥离导致 MissingDateHeader,
            // 故发标准大写 Date, 并附带 x-bce-date 双保险 (BOS 规定 x-bce-date 优先)。
            head.Append($"Date: {DateTime.UtcNow.ToString("r")}\r\n");
            head.Append($"x-bce-date: {DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")}\r\n");
            if (!string.IsNullOrEmpty(apiKey))
                head.Append($"Authorization: Bearer {apiKey}\r\n");
            head.Append($"Content-Type: multipart/form-data; boundary={boundary}\r\n");
            head.Append($"Content-Length: {bodyBytes.Length}\r\n");
            head.Append("Connection: close\r\n\r\n");
            byte[] headBytes = Encoding.ASCII.GetBytes(head.ToString());
            return await RawHttpAsync(uri, headBytes, bodyBytes, cancellationToken);
        }

        /// <summary>手写 TCP+TLS 发送请求并读取响应(自动加超时)。</summary>
        async Task<byte[]> RawHttpAsync(Uri uri, byte[] headBytes, byte[] bodyBytes, System.Threading.CancellationToken cancellationToken)
        {
            int port = uri.Port != -1 ? uri.Port : (uri.Scheme == "https" ? 443 : 80);
            using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                using (var tcp = new System.Net.Sockets.TcpClient())
                {
                    await tcp.ConnectAsync(uri.Host, port);
                    using (Stream stream = await WrapSslIfNeeded(tcp.GetStream(), uri))
                    {
                        await stream.WriteAsync(headBytes, 0, headBytes.Length, timeoutCts.Token);
                        if (bodyBytes != null && bodyBytes.Length > 0)
                            await stream.WriteAsync(bodyBytes, 0, bodyBytes.Length, timeoutCts.Token);
                        await stream.FlushAsync(timeoutCts.Token);

                        var raw = new MemoryStream();
                        var buf = new byte[65536];
                        int read;
                        while ((read = await stream.ReadAsync(buf, 0, buf.Length, timeoutCts.Token)) > 0)
                            raw.Write(buf, 0, read);
                        return ParseHttpResponse(raw.ToArray(), out _);
                    }
                }
            }
        }

        static async Task<Stream> WrapSslIfNeeded(Stream stream, Uri uri)
        {
            if (uri.Scheme != "https") return stream;
            var ssl = new System.Net.Security.SslStream(stream, false, (sender, cert, chain, errors) => true);
            await ssl.AuthenticateAsClientAsync(uri.Host);
            return ssl;
        }

        /// <summary>解析原始 HTTP 响应字节, 返回消息体(自动处理 chunked 编码)。非 2xx 抛 OpenAIApiException。</summary>
        static byte[] ParseHttpResponse(byte[] raw, out int statusCode)
        {
            byte[] sep = { 13, 10, 13, 10 }; // \r\n\r\n
            int headEnd = -1;
            for (int i = 0; i + 3 < raw.Length; i++)
            {
                if (raw[i] == sep[0] && raw[i + 1] == sep[1] && raw[i + 2] == sep[2] && raw[i + 3] == sep[3]) { headEnd = i; break; }
            }
            if (headEnd < 0) throw new InvalidOperationException("HTTP 响应缺少头结束符");
            string headText = Encoding.UTF8.GetString(raw, 0, headEnd);

            string firstLine = headText.Substring(0, headText.IndexOf('\r'));
            string[] parts = firstLine.Split(' ');
            statusCode = parts.Length > 1 && int.TryParse(parts[1], out int c) ? c : 0;

            bool chunked = headText.IndexOf("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase) >= 0
                           && headText.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0;

            var bodyRaw = new List<byte>();
            for (int i = headEnd + 4; i < raw.Length; i++) bodyRaw.Add(raw[i]);

            byte[] content = chunked ? Dechunk(bodyRaw.ToArray()) : bodyRaw.ToArray();

            if (statusCode < 200 || statusCode >= 300)
                throw new OpenAIApiException(statusCode, firstLine, Encoding.UTF8.GetString(content));
            return content;
        }

        /// <summary>解码 HTTP chunked 传输编码。</summary>
        static byte[] Dechunk(byte[] data)
        {
            var result = new MemoryStream();
            int pos = 0;
            while (pos < data.Length)
            {
                // 读取 chunk 长度行 (16进制)
                int lineEnd = pos;
                while (lineEnd + 1 < data.Length && !(data[lineEnd] == 13 && data[lineEnd + 1] == 10)) lineEnd++;
                string lenStr = Encoding.ASCII.GetString(data, pos, lineEnd - pos).Trim();
                int semicolon = lenStr.IndexOf(';');
                if (semicolon >= 0) lenStr = lenStr.Substring(0, semicolon);
                if (!int.TryParse(lenStr, System.Globalization.NumberStyles.HexNumber, null, out int len)) break;
                pos = lineEnd + 2;
                if (len == 0) break;
                result.Write(data, pos, len);
                pos += len + 2; // 跳过 trailing CRLF
            }
            return result.ToArray();
        }

        /// <summary>GET baseUrl + path, 返回响应体文本。</summary>
        public async Task<string> GetStringAsync(string path, System.Threading.CancellationToken cancellationToken = default)
        {
            using (var req = NewGet(baseUrl + path))
            {
                await SendAsync(req, cancellationToken);
                return req.downloadHandler.text;
            }
        }

        /// <summary>
        /// GET 任意 URL, 返回响应字节(如下载 GLB 文件)。
        /// 同样使用手写 TCP 报文 —— file_url 指向百度 BOS, 下载也强制要求 Date 头。
        /// 仅当目标与 baseUrl 同主机时才附带 Authorization(外部预签名 URL 不需要)。
        /// </summary>
        public async Task<byte[]> GetBytesAsync(string url, System.Threading.CancellationToken cancellationToken = default)
        {
            var uri = new Uri(url);
            bool sameHost = string.Equals(uri.Host, new Uri(baseUrl).Host, StringComparison.OrdinalIgnoreCase);
            var head = new StringBuilder(256);
            head.Append($"GET {uri.PathAndQuery} HTTP/1.1\r\n");
            head.Append($"Host: {uri.Host}\r\n");
            head.Append($"Date: {DateTime.UtcNow.ToString("r")}\r\n");
            if (sameHost && !string.IsNullOrEmpty(apiKey))
                head.Append($"Authorization: Bearer {apiKey}\r\n");
            head.Append("Accept: */*\r\n");
            head.Append("Connection: close\r\n\r\n");
            return await RawHttpAsync(uri, Encoding.ASCII.GetBytes(head.ToString()), null, cancellationToken);
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
