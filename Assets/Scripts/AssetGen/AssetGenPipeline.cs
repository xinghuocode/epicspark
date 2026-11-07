// 真实资产生成链路：z-image-turbo 文生图 → RMBG-2.0 背景透明化 → Hunyuan3D-2 图生 3D。
// 设计文档：搭建 Agent 把提示词交给本链路；不做抠图时 Hunyuan3D-2 往往只会生成一个方盒。
// 约束：
//   · 只用 UnityEngine + UnityWebRequest，不用 HttpClient / Newtonsoft / System.Text.Json；
//   · JSON 用 JsonUtility（对象根）或手写拼接，响应里字段名变化用字符扫描兜底；
//   · 单步失败返回明确 error/stage，绝不让异常穿透到搭建 Agent。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace EpicSpark.AssetGen
{
    /// <summary>一次生成链路的结果。success=false 时 stage/error 说明失败在哪一步。</summary>
    public sealed class AssetGenResult
    {
        public bool success;
        public string stage = string.Empty;
        public string error = string.Empty;
        public string glbPath = string.Empty;
        public string transparentImagePath = string.Empty;
        public string prompt = string.Empty;
        public readonly List<string> warnings = new List<string>();
    }

    /// <summary>三段式资产生成管线（可单独调用，方便在编辑器里逐步调试）。</summary>
    public sealed class AssetGenPipeline
    {
        private readonly AssetGenSettings settings;

        public AssetGenPipeline(AssetGenSettings settings)
        {
            this.settings = settings != null ? settings : new AssetGenSettings();
        }

        public AssetGenSettings Settings
        {
            get { return settings; }
        }

        /// <summary>串起文生图 → 抠图 → 图生 3D，并把生成文件落到 persistentDataPath/EpicSpark/generated。</summary>
        public async Task<AssetGenResult> RunAsync(string prompt, string category, CancellationToken ct)
        {
            var result = new AssetGenResult { prompt = prompt ?? string.Empty };
            if (!settings.HasRemote)
            {
                result.stage = "config";
                result.error = "未配置 baseUrl/apiKey/模型名，跳过远端生成链路。";
                return result;
            }

            string imagePrompt = settings.BuildImagePrompt(prompt);
            Debug.Log("[资产生成] 开始远端链路：" + imagePrompt);

            // ── 1) 文生图 ──
            byte[] opaqueBytes;
            try
            {
                opaqueBytes = await TextToImageAsync(imagePrompt, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                result.stage = "text2image";
                result.error = "文生图失败: " + e.Message;
                Debug.LogWarning("[资产生成] " + result.error);
                return result;
            }
            if (opaqueBytes == null || opaqueBytes.Length == 0)
            {
                result.stage = "text2image";
                result.error = "文生图返回空图片字节。";
                Debug.LogWarning("[资产生成] " + result.error);
                return result;
            }

            // ── 2) RMBG-2.0 抠图（失败降级用原图，但会警告可能生成方盒）──
            byte[] transparentBytes = opaqueBytes;
            if (settings.removeBackground)
            {
                try
                {
                    byte[] cut = await RemoveBackgroundAsync(opaqueBytes, ct);
                    if (cut != null && cut.Length > 0) transparentBytes = cut;
                    else result.warnings.Add("RMBG 抠图返回空，使用原图继续（Hunyuan3D 可能只生成方盒）。");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    result.warnings.Add("RMBG 抠图失败，使用原图继续（Hunyuan3D 可能只生成方盒）: " + e.Message);
                    Debug.LogWarning("[资产生成] " + result.warnings[result.warnings.Count - 1]);
                }
            }

            result.transparentImagePath = SaveDebugImage(transparentBytes, "cutout");
            for (int i = 0; i < result.warnings.Count; i++) Debug.LogWarning("[资产生成] " + result.warnings[i]);

            // ── 3) Hunyuan3D-2 图生 3D ──
            try
            {
                result.glbPath = await ImageTo3DAsync(transparentBytes, category, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                result.stage = "image23d";
                result.error = "图生 3D 失败: " + e.Message;
                Debug.LogWarning("[资产生成] " + result.error);
                return result;
            }

            if (string.IsNullOrEmpty(result.glbPath) || !File.Exists(result.glbPath))
            {
                result.stage = "image23d";
                result.error = "Hunyuan3D 没有返回可用的 3D 文件。";
                Debug.LogWarning("[资产生成] " + result.error);
                return result;
            }

            result.success = true;
            result.stage = "done";
            Debug.Log("[资产生成] 远端链路完成: " + result.glbPath);
            return result;
        }

        // ─────────────────────────── 单步 ───────────────────────────

        /// <summary>z-image-turbo 文生图，返回图片字节（b64_json 优先，url 次之）。</summary>
        public async Task<byte[]> TextToImageAsync(string prompt, CancellationToken ct)
        {
            string url = settings.ResolveUrl(settings.textToImagePath);
            var body = new StringBuilder(512);
            body.Append('{');
            body.Append("\"model\":\"").Append(AssetGenJson.Escape(settings.textToImageModel)).Append("\",");
            body.Append("\"prompt\":\"").Append(AssetGenJson.Escape(prompt)).Append("\",");
            body.Append("\"n\":1,");
            body.Append("\"size\":\"").Append(settings.imageSize.ToString(CultureInfo.InvariantCulture)).Append('x')
                .Append(settings.imageSize.ToString(CultureInfo.InvariantCulture)).Append("\",");
            body.Append("\"response_format\":\"b64_json\"");
            body.Append('}');

            string response = await AssetGenHttp.PostJsonAsync(url, settings.apiKey, body.ToString(), settings.requestTimeoutSeconds, ct);
            string remoteUrl;
            byte[] bytes = ParseImageBytes(response, out remoteUrl);
            if (bytes != null && bytes.Length > 0) return bytes;
            if (!string.IsNullOrEmpty(remoteUrl))
                return await AssetGenHttp.GetBytesAsync(remoteUrl, settings.apiKey, settings.requestTimeoutSeconds, ct);
            throw new IOException("文生图响应里既没有 b64_json 也没有 url: " + AssetGenJson.Truncate(response, 240));
        }

        /// <summary>RMBG-2.0 背景透明化，返回透明 PNG 字节。</summary>
        public async Task<byte[]> RemoveBackgroundAsync(byte[] png, CancellationToken ct)
        {
            if (png == null || png.Length == 0) throw new IOException("抠图输入为空。");
            string url = settings.ResolveUrl(settings.backgroundRemovalPath);
            var body = new StringBuilder(png.Length * 2 + 256);
            body.Append('{');
            body.Append("\"model\":\"").Append(AssetGenJson.Escape(settings.backgroundRemovalModel)).Append("\",");
            body.Append("\"image\":\"data:image/png;base64,").Append(Convert.ToBase64String(png)).Append("\"");
            body.Append('}');

            string response = await AssetGenHttp.PostJsonAsync(url, settings.apiKey, body.ToString(), settings.requestTimeoutSeconds, ct);
            string remoteUrl;
            byte[] bytes = ParseImageBytes(response, out remoteUrl);
            if (bytes != null && bytes.Length > 0) return bytes;
            if (!string.IsNullOrEmpty(remoteUrl))
                return await AssetGenHttp.GetBytesAsync(remoteUrl, settings.apiKey, settings.requestTimeoutSeconds, ct);
            throw new IOException("抠图响应里没有图片数据: " + AssetGenJson.Truncate(response, 240));
        }

        /// <summary>Hunyuan3D-2 提交 + 轮询 + 下载，返回本地 glb 绝对路径。</summary>
        public async Task<string> ImageTo3DAsync(byte[] png, string category, CancellationToken ct)
        {
            if (png == null || png.Length == 0) throw new IOException("图生 3D 输入为空。");
            string submitUrl = settings.ResolveUrl(settings.imageTo3DPath);
            var body = new StringBuilder(png.Length * 2 + 256);
            body.Append('{');
            body.Append("\"model\":\"").Append(AssetGenJson.Escape(settings.imageTo3DModel)).Append("\",");
            body.Append("\"image\":\"data:image/png;base64,").Append(Convert.ToBase64String(png)).Append("\",");
            body.Append("\"texture\":\"auto\",");
            body.Append("\"num_inference_steps\":50");
            body.Append('}');

            string submit = await AssetGenHttp.PostJsonAsync(submitUrl, settings.apiKey, body.ToString(), settings.requestTimeoutSeconds, ct);

            // 有些部署会同步直接给文件地址
            string direct = AssetGenJson.ExtractString(submit, "file_url");
            if (string.IsNullOrEmpty(direct)) direct = AssetGenJson.ExtractString(submit, "glb_url");
            if (string.IsNullOrEmpty(direct)) direct = AssetGenJson.ExtractString(submit, "model_url");
            if (!string.IsNullOrEmpty(direct)) return await DownloadGlbAsync(direct, ct);

            string taskId = AssetGenJson.ExtractIdentifier(submit);
            if (string.IsNullOrEmpty(taskId))
                throw new IOException("图生 3D 提交响应里没有任务 id: " + AssetGenJson.Truncate(submit, 240));

            string pollUrl = submitUrl + "/" + Uri.EscapeDataString(taskId);
            int interval = Mathf.Max(1, settings.pollIntervalSeconds);
            int waited = 0;
            int timeout = Mathf.Max(interval, settings.pollTimeoutSeconds);
            while (waited < timeout)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(interval * 1000, ct);
                waited += interval;

                string poll = await AssetGenHttp.GetTextAsync(pollUrl, settings.apiKey, settings.requestTimeoutSeconds, ct);
                string status = AssetGenJson.ExtractString(poll, "status");
                if (string.IsNullOrEmpty(status)) status = AssetGenJson.ExtractString(poll, "task_status");
                if (string.IsNullOrEmpty(status)) status = AssetGenJson.ExtractString(poll, "state");

                string fileUrl = AssetGenJson.ExtractString(poll, "file_url");
                if (string.IsNullOrEmpty(fileUrl)) fileUrl = AssetGenJson.ExtractString(poll, "glb_url");
                if (string.IsNullOrEmpty(fileUrl)) fileUrl = AssetGenJson.ExtractString(poll, "model_url");
                if (string.IsNullOrEmpty(fileUrl)) fileUrl = AssetGenJson.ExtractString(poll, "url");
                if (!string.IsNullOrEmpty(fileUrl) && LooksLikeModelUrl(fileUrl))
                    return await DownloadGlbAsync(fileUrl, ct);

                if (AssetGenJson.IsFailureStatus(status))
                    throw new IOException("Hunyuan3D 任务失败，状态=" + status + " | " + AssetGenJson.Truncate(poll, 160));
                if (AssetGenJson.IsFinishedStatus(status) && string.IsNullOrEmpty(fileUrl))
                    throw new IOException("Hunyuan3D 任务已完成但没有文件地址: " + AssetGenJson.Truncate(poll, 240));
            }
            throw new TimeoutException("Hunyuan3D 任务轮询超时（" + timeout + " 秒，任务 " + taskId + "）。");
        }

        private async Task<string> DownloadGlbAsync(string url, CancellationToken ct)
        {
            byte[] bytes = await AssetGenHttp.GetBytesAsync(url, settings.apiKey, settings.requestTimeoutSeconds, ct);
            if (bytes == null || bytes.Length == 0) throw new IOException("3D 文件下载为空: " + url);
            string directory = Path.Combine(Application.persistentDataPath, "EpicSpark", "generated");
            Directory.CreateDirectory(directory);
            string fileName = "gen_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture) + ".glb";
            string path = Path.Combine(directory, fileName);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        private static bool LooksLikeModelUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            string lower = url.ToLowerInvariant();
            return lower.EndsWith(".glb") || lower.EndsWith(".gltf") || lower.EndsWith(".zip")
                || lower.Contains("/file") || lower.Contains("download") || lower.Contains("model");
        }

        // ─────────────────────────── 响应解析 ───────────────────────────

        private static byte[] ParseImageBytes(string json, out string remoteUrl)
        {
            remoteUrl = string.Empty;
            if (string.IsNullOrEmpty(json)) return null;

            string[] base64Fields = { "b64_json", "base64", "image_base64", "image", "output", "data" };
            for (int i = 0; i < base64Fields.Length; i++)
            {
                string value = AssetGenJson.ExtractString(json, base64Fields[i]);
                if (string.IsNullOrEmpty(value)) continue;
                byte[] decoded = AssetGenJson.TryDecodeBase64(value);
                if (decoded != null && decoded.Length > 0) return decoded;
            }

            string[] urlFields = { "url", "image_url", "file_url", "output_url", "result_url" };
            for (int i = 0; i < urlFields.Length; i++)
            {
                string value = AssetGenJson.ExtractString(json, urlFields[i]);
                if (!string.IsNullOrEmpty(value) && (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                {
                    remoteUrl = value;
                    break;
                }
            }
            return null;
        }

        private string SaveDebugImage(byte[] png, string tag)
        {
            if (png == null || png.Length == 0) return string.Empty;
            try
            {
                string directory = Path.Combine(Application.persistentDataPath, "EpicSpark", "generated");
                Directory.CreateDirectory(directory);
                string fileName = "gen_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture)
                    + "_" + tag + ".png";
                string path = Path.Combine(directory, fileName);
                File.WriteAllBytes(path, png);
                return path;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[资产生成] 保存中间图片失败: " + e.Message);
                return string.Empty;
            }
        }
    }

    // ─────────────────────────── HTTP ───────────────────────────

    /// <summary>UnityWebRequest 的 async 封装（Unity 2022 没有原生 awaitable，用 while(!op.isDone)+Task.Yield 等待）。</summary>
    internal static class AssetGenHttp
    {
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
                        + " " + request.error + " | " + AssetGenJson.Truncate(text, 300));
                }
                return text;
            }
        }

        public static async Task<string> GetTextAsync(string url, string apiKey, int timeoutSeconds, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(url)) throw new InvalidOperationException("请求 URL 为空。");
            using (var request = UnityWebRequest.Get(url))
            {
                request.downloadHandler = new DownloadHandlerBuffer();
                request.timeout = Mathf.Max(5, timeoutSeconds);
                if (!string.IsNullOrEmpty(apiKey)) request.SetRequestHeader("Authorization", "Bearer " + apiKey);

                await SendAsync(request, ct);
                string text = request.downloadHandler != null ? request.downloadHandler.text : string.Empty;
                if (request.result != UnityWebRequest.Result.Success)
                {
                    throw new IOException("GET " + url + " 失败 HTTP " + (int)request.responseCode
                        + " " + request.error + " | " + AssetGenJson.Truncate(text, 300));
                }
                return text;
            }
        }

        public static async Task<byte[]> GetBytesAsync(string url, string apiKey, int timeoutSeconds, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(url)) throw new InvalidOperationException("请求 URL 为空。");
            using (var request = UnityWebRequest.Get(url))
            {
                request.downloadHandler = new DownloadHandlerBuffer();
                request.timeout = Mathf.Max(5, timeoutSeconds);
                if (!string.IsNullOrEmpty(apiKey)) request.SetRequestHeader("Authorization", "Bearer " + apiKey);

                await SendAsync(request, ct);
                if (request.result != UnityWebRequest.Result.Success)
                {
                    throw new IOException("下载 " + url + " 失败 HTTP " + (int)request.responseCode + " " + request.error);
                }
                return request.downloadHandler != null ? request.downloadHandler.data : null;
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
    }

    // ─────────────────────────── JSON 辅助 ───────────────────────────

    /// <summary>手写 JSON 的转义与「按字段名扫描取值」。JsonUtility 不支持顶层数组/字典，响应格式又可能变化，故需要它。</summary>
    internal static class AssetGenJson
    {
        public static string Escape(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var builder = new StringBuilder(text.Length + 8);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                switch (c)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (c < 0x20) builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else builder.Append(c);
                        break;
                }
            }
            return builder.ToString();
        }

        /// <summary>扫描 JSON 里第一个 "field": "value" 并返回反转义后的字符串；找不到返回 null。</summary>
        public static string ExtractString(string json, string fieldName)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(fieldName)) return null;
            string key = "\"" + fieldName + "\"";
            int index = json.IndexOf(key, StringComparison.Ordinal);
            if (index < 0) return null;
            int colon = json.IndexOf(':', index + key.Length);
            if (colon < 0) return null;
            int quote = json.IndexOf('"', colon + 1);
            if (quote < 0) return null;

            var builder = new StringBuilder(64);
            for (int i = quote + 1; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '\\')
                {
                    if (i + 1 >= json.Length) break;
                    i++;
                    char next = json[i];
                    switch (next)
                    {
                        case 'n': builder.Append('\n'); break;
                        case 'r': builder.Append('\r'); break;
                        case 't': builder.Append('\t'); break;
                        case 'b': builder.Append('\b'); break;
                        case 'f': builder.Append('\f'); break;
                        case '"': builder.Append('"'); break;
                        case '\\': builder.Append('\\'); break;
                        case '/': builder.Append('/'); break;
                        case 'u':
                            {
                                if (i + 4 >= json.Length) { i = json.Length; break; }
                                int code;
                                if (int.TryParse(json.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                                    builder.Append((char)code);
                                i += 4;
                                break;
                            }
                        default: builder.Append(next); break;
                    }
                    continue;
                }
                if (c == '"') return builder.ToString();
                builder.Append(c);
            }
            return null;
        }

        /// <summary>取任务 id：id / task_id / request_id 等常见字段。</summary>
        public static string ExtractIdentifier(string json)
        {
            string[] fields = { "id", "task_id", "taskId", "request_id", "requestId", "job_id", "jobId" };
            for (int i = 0; i < fields.Length; i++)
            {
                string value = ExtractString(json, fields[i]);
                if (!string.IsNullOrEmpty(value)) return value;
            }
            return null;
        }

        public static bool TryDecodeBase64(string value, out byte[] bytes)
        {
            bytes = null;
            if (string.IsNullOrEmpty(value)) return false;
            string data = value.Trim();
            int comma = data.IndexOf(',');
            if (data.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0)
                data = data.Substring(comma + 1);
            data = data.Replace("\r", string.Empty).Replace("\n", string.Empty).Replace(" ", string.Empty);
            if (data.Length == 0) return false;
            try
            {
                bytes = Convert.FromBase64String(data);
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        public static byte[] TryDecodeBase64(string value)
        {
            byte[] bytes;
            return TryDecodeBase64(value, out bytes) ? bytes : null;
        }

        public static bool IsFailureStatus(string status)
        {
            if (string.IsNullOrEmpty(status)) return false;
            string lower = status.ToLowerInvariant();
            return lower.Contains("fail") || lower.Contains("error") || lower.Contains("cancel") || lower.Contains("reject");
        }

        public static bool IsFinishedStatus(string status)
        {
            if (string.IsNullOrEmpty(status)) return false;
            string lower = status.ToLowerInvariant();
            return lower.Contains("success") || lower.Contains("succeed") || lower.Contains("complete")
                || lower.Contains("done") || lower.Contains("finish") || lower == "ok";
        }

        public static string Truncate(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            string single = text.Replace('\n', ' ').Replace('\r', ' ');
            return single.Length <= maxLength ? single : single.Substring(0, maxLength) + "…";
        }
    }
}
