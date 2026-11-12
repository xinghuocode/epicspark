// 资产生成链路的 HTTP 传输层。
//
// 为什么不全部用 UnityWebRequest：
//   gitee 的「图生 3D 提交」与「GLB 文件下载」最终落在百度 BOS，后端强制要求 HTTP `Date` 头；
//   而 UnityWebRequest / Mono HttpClient 在部分编辑器环境下会丢弃自定义 Date / x-bce-date 头，
//   导致 400 MissingDateHeader。因此 multipart 提交与文件下载走手写 TCP + TLS 报文，
//   保证请求头原样到达服务端；纯 JSON 的文生图与任务轮询仍用 UnityWebRequest。
//
// 本文件来自工程内可工作的 wtz 场景 Agent 测试床（ModelGenerationPipeline + OpenAIClient 的
// 通用 HTTP 部分），按本工程 AssetGen 目录整理，不引入任何第三方包。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace EpicSpark.AssetGen
{
    internal static class AssetGenHttp
    {
        // ─────────────────────────── UnityWebRequest（JSON / 轮询） ───────────────────────────

        /// <summary>POST application/json，返回响应文本；HTTP 非 2xx 抛 IOException。</summary>
        public static async Task<string> PostJsonAsync(string url, string apiKey, string json, int timeoutSeconds, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(url)) throw new InvalidOperationException("请求 URL 为空。");
            byte[] payload = Encoding.UTF8.GetBytes(json ?? string.Empty);
            using (var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
            {
                request.uploadHandler = new UploadHandlerRaw(payload);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.timeout = Mathf.Max(5, timeoutSeconds);
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("Accept", "application/json");
                if (!string.IsNullOrEmpty(apiKey)) request.SetRequestHeader("Authorization", "Bearer " + apiKey);

                await SendAsync(request, ct);
                string text = request.downloadHandler != null ? request.downloadHandler.text : string.Empty;
                if (request.result != UnityWebRequest.Result.Success)
                {
                    throw new IOException("POST " + url + " 失败 HTTP " + (int)request.responseCode
                        + " " + request.error + " | " + Truncate(text, 300));
                }
                return text;
            }
        }

        /// <summary>GET，返回响应文本（用于异步任务轮询）；HTTP 非 2xx 抛 IOException。</summary>
        public static async Task<string> GetTextAsync(string url, string apiKey, int timeoutSeconds, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(url)) throw new InvalidOperationException("请求 URL 为空。");
            using (var request = UnityWebRequest.Get(url))
            {
                request.downloadHandler = new DownloadHandlerBuffer();
                request.timeout = Mathf.Max(5, timeoutSeconds);
                request.SetRequestHeader("Accept", "application/json");
                if (!string.IsNullOrEmpty(apiKey)) request.SetRequestHeader("Authorization", "Bearer " + apiKey);

                await SendAsync(request, ct);
                string text = request.downloadHandler != null ? request.downloadHandler.text : string.Empty;
                if (request.result != UnityWebRequest.Result.Success)
                {
                    throw new IOException("GET " + url + " 失败 HTTP " + (int)request.responseCode
                        + " " + request.error + " | " + Truncate(text, 300));
                }
                return text;
            }
        }

        private static async Task SendAsync(UnityWebRequest request, CancellationToken ct)
        {
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
        }

        // ─────────────────────────── 手写 TCP + TLS（multipart / 文件下载） ───────────────────────────

        /// <summary>
        /// POST multipart/form-data。fields 为普通表单字段，fileBytes 作为 fileFieldName 的文件字段。
        /// 返回响应体字节（可能是 JSON，也可能是直接图像二进制）；HTTP 非 2xx 抛 IOException。
        /// </summary>
        public static async Task<byte[]> PostMultipartAsync(
            string url,
            string apiKey,
            Dictionary<string, string> fields,
            byte[] fileBytes,
            string fileFieldName,
            string fileName,
            string fileMime,
            int timeoutSeconds,
            CancellationToken ct)
        {
            if (string.IsNullOrEmpty(url)) throw new InvalidOperationException("请求 URL 为空。");

            string boundary = "----EpicSpark" + Guid.NewGuid().ToString("N");
            byte[] body = BuildMultipartBody(boundary, fields, fileBytes, fileFieldName, fileName, fileMime);

            var request = new RawRequest
            {
                Uri = new Uri(url),
                Method = "POST",
                Body = body,
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Content-Type"] = "multipart/form-data; boundary=" + boundary,
                    ["Accept"] = "application/json",
                },
            };
            if (!string.IsNullOrEmpty(apiKey)) request.Headers["Authorization"] = "Bearer " + apiKey;

            return await SendRawAsync(request, timeoutSeconds, ct);
        }

        /// <summary>
        /// GET 任意 URL 并返回字节（下载 GLB）。预签名 URL 与 baseUrl 不同主机时不附带 Authorization。
        /// 同样手写报文：BOS 下载要求 Date 头。
        /// </summary>
        public static async Task<byte[]> GetBytesAsync(string url, string apiKey, string baseUrl, int timeoutSeconds, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(url)) throw new InvalidOperationException("请求 URL 为空。");

            var uri = new Uri(url);
            var request = new RawRequest
            {
                Uri = uri,
                Method = "GET",
                Body = null,
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Accept"] = "*/*" },
            };
            try
            {
                if (!string.IsNullOrEmpty(apiKey) && !string.IsNullOrEmpty(baseUrl)
                    && string.Equals(uri.Host, new Uri(baseUrl).Host, StringComparison.OrdinalIgnoreCase))
                {
                    request.Headers["Authorization"] = "Bearer " + apiKey;
                }
            }
            catch (UriFormatException)
            {
                // baseUrl 非法时不带鉴权，交给服务端返回 401/403。
            }

            return await SendRawAsync(request, timeoutSeconds, ct);
        }

        private sealed class RawRequest
        {
            public Uri Uri;
            public string Method;
            public Dictionary<string, string> Headers;
            public byte[] Body;
        }

        private sealed class RawResponse
        {
            public int status;
            public string headers;
            public byte[] body;
        }

        private static async Task<byte[]> SendRawAsync(RawRequest request, int timeoutSeconds, CancellationToken ct)
        {
            int limit = Mathf.Max(5, timeoutSeconds);
            Uri target = request.Uri;

            for (int hop = 0; hop < 4; hop++)
            {
                var attempt = new RawRequest
                {
                    Uri = target,
                    Method = request.Method,
                    Headers = request.Headers,
                    Body = request.Body,
                };
                RawResponse response = await SendOnceAsync(attempt, limit, ct);

                if (response.status == 301 || response.status == 302 || response.status == 303
                    || response.status == 307 || response.status == 308)
                {
                    string location = GetHeader(response.headers, "Location");
                    if (string.IsNullOrEmpty(location)) throw new IOException("HTTP " + response.status + " 缺少 Location: " + Truncate(Encoding.UTF8.GetString(response.body ?? new byte[0]), 200));
                    target = new Uri(request.Uri, location);
                    // 303 按规范改为 GET；其余保留原方法。
                    if (response.status == 303)
                    {
                        request.Method = "GET";
                        request.Body = null;
                        request.Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Accept"] = "*/*" };
                        request.Headers["Accept"] = "*/*";
                    }
                    continue;
                }

                if (response.status < 200 || response.status >= 300)
                {
                    string text = response.body != null ? Encoding.UTF8.GetString(response.body) : string.Empty;
                    throw new IOException("HTTP " + response.status + " " + Truncate(text, 300));
                }
                return response.body ?? new byte[0];
            }
            throw new IOException("重定向次数过多: " + request.Uri);
        }

        private static async Task<RawResponse> SendOnceAsync(RawRequest request, int timeoutSeconds, CancellationToken ct)
        {
            Uri uri = request.Uri;
            int port = uri.Port != -1 ? uri.Port : (uri.Scheme == Uri.UriSchemeHttps ? 443 : 80);
            byte[] head = BuildHead(request);

            using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                try
                {
                    using (var tcp = new TcpClient())
                    {
                        Task connect = tcp.ConnectAsync(uri.Host, port);
                        Task delay = Task.Delay(TimeSpan.FromSeconds(Math.Min(30, timeoutSeconds)), timeoutCts.Token);
                        if (await Task.WhenAny(connect, delay) != connect)
                        {
                            timeoutCts.Token.ThrowIfCancellationRequested();
                            throw new TimeoutException("连接 " + uri.Host + " 超时");
                        }
                        await connect;

                        using (Stream network = tcp.GetStream())
                        using (Stream stream = await WrapSslIfNeededAsync(network, uri))
                        {
                            await stream.WriteAsync(head, 0, head.Length, timeoutCts.Token);
                            if (request.Body != null && request.Body.Length > 0)
                                await stream.WriteAsync(request.Body, 0, request.Body.Length, timeoutCts.Token);
                            await stream.FlushAsync(timeoutCts.Token);

                            var raw = new MemoryStream();
                            var buffer = new byte[65536];
                            int read;
                            while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, timeoutCts.Token)) > 0)
                                raw.Write(buffer, 0, read);

                            return ParseHttpResponse(raw.ToArray());
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    if (ct.IsCancellationRequested) throw;
                    throw new TimeoutException("请求超时（" + timeoutSeconds + " 秒）: " + uri);
                }
                catch (IOException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    throw new IOException("请求 " + uri + " 失败: " + e.Message, e);
                }
            }
        }

        private static async Task<Stream> WrapSslIfNeededAsync(Stream stream, Uri uri)
        {
            if (uri.Scheme != Uri.UriSchemeHttps) return stream;
            // 使用系统默认校验会因 Mono/Unity 的 CA 存储差异在部分机器上失败；
            // 这里沿用测试床的做法只做加密传输（目标固定为配置里的 gitee/BOS 端点）。
            var ssl = new SslStream(stream, false, delegate { return true; });
            await ssl.AuthenticateAsClientAsync(uri.Host);
            return ssl;
        }

        private static byte[] BuildHead(RawRequest request)
        {
            Uri uri = request.Uri;
            var head = new StringBuilder(512);
            head.Append(request.Method).Append(' ').Append(string.IsNullOrEmpty(uri.PathAndQuery) ? "/" : uri.PathAndQuery)
                .Append(" HTTP/1.1\r\n");
            head.Append("Host: ").Append(uri.Host).Append("\r\n");
            // BOS 强制要求 Date；x-bce-date 为双保险（BOS 规定 x-bce-date 优先）。
            head.Append("Date: ").Append(DateTime.UtcNow.ToString("r", CultureInfo.InvariantCulture)).Append("\r\n");
            head.Append("x-bce-date: ").Append(DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)).Append("\r\n");
            if (request.Headers != null)
            {
                foreach (KeyValuePair<string, string> pair in request.Headers)
                {
                    if (string.IsNullOrEmpty(pair.Value)) continue;
                    head.Append(pair.Key).Append(": ").Append(pair.Value).Append("\r\n");
                }
            }
            head.Append("Accept-Encoding: identity\r\n");
            head.Append("User-Agent: EpicSpark/1.0\r\n");
            // 与实测实现保持一致：GET（body=null）不发 Content-Length，POST 才发。
            if (request.Body != null)
                head.Append("Content-Length: ").Append(request.Body.Length).Append("\r\n");
            head.Append("Connection: close\r\n\r\n");
            return Encoding.ASCII.GetBytes(head.ToString());
        }

        private static byte[] BuildMultipartBody(
            string boundary,
            Dictionary<string, string> fields,
            byte[] fileBytes,
            string fileFieldName,
            string fileName,
            string fileMime)
        {
            using (var body = new MemoryStream())
            {
                Action<string> writeText = delegate (string text)
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(text);
                    body.Write(bytes, 0, bytes.Length);
                };

                if (fields != null)
                {
                    foreach (KeyValuePair<string, string> pair in fields)
                    {
                        writeText("--" + boundary + "\r\n");
                        writeText("Content-Disposition: form-data; name=\"" + pair.Key + "\"\r\n\r\n");
                        writeText(pair.Value ?? string.Empty);
                        writeText("\r\n");
                    }
                }

                if (fileBytes != null && fileBytes.Length > 0)
                {
                    writeText("--" + boundary + "\r\n");
                    writeText("Content-Disposition: form-data; name=\"" + fileFieldName + "\"; filename=\""
                              + (string.IsNullOrEmpty(fileName) ? "image.png" : fileName) + "\"\r\n");
                    writeText("Content-Type: " + (string.IsNullOrEmpty(fileMime) ? "application/octet-stream" : fileMime) + "\r\n\r\n");
                    body.Write(fileBytes, 0, fileBytes.Length);
                    writeText("\r\n");
                }

                writeText("--" + boundary + "--\r\n");
                return body.ToArray();
            }
        }

        // ─────────────────────────── 响应解析 ───────────────────────────

        private static RawResponse ParseHttpResponse(byte[] raw)
        {
            int headEnd = IndexOf(raw, new byte[] { 13, 10, 13, 10 }, 0);
            int sepLength = 4;
            if (headEnd < 0)
            {
                headEnd = IndexOf(raw, new byte[] { 10, 10 }, 0);
                sepLength = 2;
            }
            if (headEnd < 0) throw new IOException("HTTP 响应缺少头结束符");

            string headText = Encoding.UTF8.GetString(raw, 0, headEnd);
            int firstLineEnd = headText.IndexOf('\r');
            if (firstLineEnd < 0) firstLineEnd = headText.IndexOf('\n');
            string firstLine = firstLineEnd > 0 ? headText.Substring(0, firstLineEnd) : headText;

            int status = 0;
            string[] parts = firstLine.Split(' ');
            if (parts.Length > 1) int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out status);

            bool chunked = headText.IndexOf("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase) >= 0
                           && headText.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0;

            int bodyStart = headEnd + sepLength;
            var body = new byte[Math.Max(0, raw.Length - bodyStart)];
            if (body.Length > 0) Array.Copy(raw, bodyStart, body, 0, body.Length);
            if (chunked) body = Dechunk(body);

            return new RawResponse { status = status, headers = headText, body = body };
        }

        private static string GetHeader(string headers, string name)
        {
            if (string.IsNullOrEmpty(headers) || string.IsNullOrEmpty(name)) return string.Empty;
            string[] lines = headers.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            string prefix = name + ":";
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return line.Substring(prefix.Length).Trim();
            }
            return string.Empty;
        }

        private static int IndexOf(byte[] data, byte[] pattern, int start)
        {
            if (data == null || pattern == null || pattern.Length == 0) return -1;
            for (int i = start; i <= data.Length - pattern.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (data[i + j] != pattern[j]) { match = false; break; }
                }
                if (match) return i;
            }
            return -1;
        }

        /// <summary>解码 HTTP chunked 传输编码。</summary>
        private static byte[] Dechunk(byte[] data)
        {
            using (var result = new MemoryStream())
            {
                int pos = 0;
                while (pos < data.Length)
                {
                    int lineEnd = pos;
                    while (lineEnd + 1 < data.Length && !(data[lineEnd] == 13 && data[lineEnd + 1] == 10)) lineEnd++;
                    string lengthText = Encoding.ASCII.GetString(data, pos, lineEnd - pos).Trim();
                    int semicolon = lengthText.IndexOf(';');
                    if (semicolon >= 0) lengthText = lengthText.Substring(0, semicolon);
                    int length;
                    if (!int.TryParse(lengthText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out length)) break;
                    pos = lineEnd + 2;
                    if (length == 0) break;
                    if (pos + length > data.Length) break;
                    result.Write(data, pos, length);
                    pos += length + 2; // 跳过 chunk 末尾的 CRLF
                }
                return result.ToArray();
            }
        }

        private static string Truncate(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            string single = text.Replace('\n', ' ').Replace('\r', ' ');
            return single.Length <= maxLength ? single : single.Substring(0, maxLength) + "…";
        }
    }
}
