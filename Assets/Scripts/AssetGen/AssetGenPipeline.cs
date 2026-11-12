// 资产生成 Agent 的真实链路（移植自工程内可工作的 wtz 场景 Agent 测试床）：
//   Z-Image-Turbo 文生图 → RMBG-2.0 背景透明化 → Hunyuan3D-2 单图生 3D → GLB 落盘。
//
// 与旧实现的差异（旧链路在 gitee 上跑不通，已实测）：
//   · 抠图端点：/images/background/removal（404）→ /images/mattings（multipart，200）
//   · 图生 3D：/3d/generations（404）→ /async/image-to-3d（multipart 提交异步任务）
//   · 轮询：提交 URL + /{id}（404）→ /task/{task_id}（200），5 秒一次、最长 15 分钟
//   · multipart 与文件下载走手写 TCP+TLS（百度 BOS 强制要求 Date 头，UnityWebRequest 会丢）
//   · JSON 用工程内 MiniJson 解析（data[0].b64_json / output.file_url 等嵌套结构）
//
// 约束保持不变：
//   · baseUrl / apiKey 未配置时绝不发请求，上层走确定性兜底；
//   · 单步失败返回明确 stage/error，异常不穿透到搭建 Agent；
//   · 抠图失败自动改用原图继续（Hunyuan3D 内部会再做 rembg）。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

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
        // 与测试床一致的实测参数：文生图 8 步；Hunyuan3D 5 步 / octree 128 / guidance 5。
        private const int TextToImageSteps = 8;
        private const int ImageTo3DSteps = 5;
        private const int OctreeResolution = 128;
        private const float GuidanceScale = 5f;
        private const int Seed = 1234;

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
            Debug.Log("[资产生成] 远端链路开始：" + imagePrompt);

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
            Debug.Log("[资产生成] 文生图完成：" + (opaqueBytes.Length / 1024) + " KB");

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

        // ─────────────────────────── 1) 文生图 ───────────────────────────

        /// <summary>Z-Image-Turbo 文生图，返回图片字节（b64_json 优先，url 次之）。</summary>
        public async Task<byte[]> TextToImageAsync(string prompt, CancellationToken ct)
        {
            string url = settings.ResolveUrl(settings.textToImagePath);
            string size = settings.imageSize.ToString(CultureInfo.InvariantCulture) + "x"
                          + settings.imageSize.ToString(CultureInfo.InvariantCulture);

            string body = MiniJson.Serialize(new Dictionary<string, object>
            {
                ["model"] = settings.textToImageModel,
                ["prompt"] = prompt,
                ["size"] = size,
                ["num_inference_steps"] = TextToImageSteps,
                ["response_format"] = "b64_json",
            });

            string response = await AssetGenHttp.PostJsonAsync(url, settings.apiKey, body, settings.requestTimeoutSeconds, ct);
            byte[] bytes = await ExtractImageFromJsonAsync(response, ct);
            if (bytes != null && bytes.Length > 0) return bytes;
            throw new IOException("文生图响应里没有图片数据: " + Truncate(response, 240));
        }

        // ─────────────────────────── 2) RMBG-2.0 抠图 ───────────────────────────

        /// <summary>
        /// RMBG-2.0 背景透明化，返回透明 PNG 字节。
        /// 端点按 settings 走 multipart；失败自动尝试兼容端点，仍失败由调用方降级用原图。
        /// </summary>
        public async Task<byte[]> RemoveBackgroundAsync(byte[] png, CancellationToken ct)
        {
            if (png == null || png.Length == 0) throw new IOException("抠图输入为空。");

            var paths = new List<string>();
            if (!string.IsNullOrEmpty(settings.backgroundRemovalPath)) paths.Add(settings.backgroundRemovalPath);
            if (!string.IsNullOrEmpty(settings.backgroundRemovalFallbackPath)
                && !string.Equals(settings.backgroundRemovalPath, settings.backgroundRemovalFallbackPath, StringComparison.OrdinalIgnoreCase))
            {
                paths.Add(settings.backgroundRemovalFallbackPath);
            }

            var errors = new List<string>();
            for (int i = 0; i < paths.Count; i++)
            {
                string url = settings.ResolveUrl(paths[i]);
                var fields = new Dictionary<string, string>
                {
                    ["model"] = settings.backgroundRemovalModel,
                    ["response_format"] = "b64_json",
                };
                try
                {
                    byte[] response = await AssetGenHttp.PostMultipartAsync(
                        url, settings.apiKey, fields, png, "image", "image.png", "image/png",
                        settings.requestTimeoutSeconds, ct);

                    byte[] image = await ExtractImageFromBytesAsync(response, ct);
                    if (image != null && image.Length > 0)
                    {
                        Debug.Log("[资产生成] 抠图完成：" + paths[i] + " → " + (image.Length / 1024) + " KB");
                        return image;
                    }
                    errors.Add(paths[i] + " 响应里没有图片数据");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    errors.Add(paths[i] + ": " + e.Message);
                }
            }
            throw new IOException("RMBG-2.0 抠图失败（已尝试 " + string.Join(" / ", errors.ToArray()) + "）");
        }

        // ─────────────────────────── 3) Hunyuan3D-2 图生 3D ───────────────────────────

        /// <summary>Hunyuan3D-2 提交 + 轮询 + 下载，返回本地 glb 绝对路径。</summary>
        public async Task<string> ImageTo3DAsync(byte[] png, string category, CancellationToken ct)
        {
            if (png == null || png.Length == 0) throw new IOException("图生 3D 输入为空。");

            string submitUrl = settings.ResolveUrl(settings.imageTo3DPath);
            var fields = new Dictionary<string, string>
            {
                ["type"] = "glb",
                ["model"] = settings.imageTo3DModel,
                ["texture"] = "true",
                ["seed"] = Seed.ToString(CultureInfo.InvariantCulture),
                ["num_inference_steps"] = ImageTo3DSteps.ToString(CultureInfo.InvariantCulture),
                ["octree_resolution"] = OctreeResolution.ToString(CultureInfo.InvariantCulture),
                ["guidance_scale"] = GuidanceScale.ToString("0.###", CultureInfo.InvariantCulture),
            };

            Debug.Log("[资产生成] 提交图生 3D 任务…");
            byte[] submitBytes = await AssetGenHttp.PostMultipartAsync(
                submitUrl, settings.apiKey, fields, png, "image", "image.png", "image/png",
                settings.requestTimeoutSeconds, ct);
            string submit = DecodeText(submitBytes);

            Dictionary<string, object> submitRoot = TryParseObject(submit);
            string directUrl = FindFileUrl(submitRoot);
            if (!string.IsNullOrEmpty(directUrl)) return await DownloadGlbAsync(directUrl, ct);

            string taskId = FindTaskId(submitRoot);
            if (string.IsNullOrEmpty(taskId))
                throw new IOException("图生 3D 提交响应里没有 task_id: " + Truncate(submit, 240));

            Debug.Log("[资产生成] 3D 任务已提交: " + taskId + "，开始轮询 " + settings.taskPath);
            string pollUrl = settings.ResolveUrl(settings.taskPath) + Uri.EscapeDataString(taskId);
            int interval = Mathf.Max(1, settings.pollIntervalSeconds);
            int timeout = Mathf.Max(interval, settings.pollTimeoutSeconds);
            int waited = 0;
            string lastStatus = "";

            while (waited < timeout)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(interval * 1000, ct);
                waited += interval;

                string poll = await AssetGenHttp.GetTextAsync(pollUrl, settings.apiKey, settings.requestTimeoutSeconds, ct);
                Dictionary<string, object> statusRoot = TryParseObject(poll);
                string status = FindStatus(statusRoot);
                lastStatus = status;

                string fileUrl = FindFileUrl(statusRoot);
                if (!string.IsNullOrEmpty(fileUrl) && (string.IsNullOrEmpty(status) || IsFinishedStatus(status)))
                    return await DownloadGlbAsync(fileUrl, ct);

                if (IsFailureStatus(status))
                    throw new IOException("Hunyuan3D 任务失败，状态=" + status + " | " + Truncate(poll, 200));

                if (IsFinishedStatus(status) && string.IsNullOrEmpty(fileUrl))
                    throw new IOException("Hunyuan3D 任务已完成但没有文件地址: " + Truncate(poll, 240));

                Debug.Log("[资产生成] 3D 任务 " + taskId + " 状态=" + (string.IsNullOrEmpty(status) ? "(未知)" : status)
                          + "，已等待 " + waited + "s");
            }
            throw new TimeoutException("Hunyuan3D 任务轮询超时（" + timeout + " 秒，任务 " + taskId
                                      + "，最后状态 " + lastStatus + "）。");
        }

        private async Task<string> DownloadGlbAsync(string url, CancellationToken ct)
        {
            int timeout = Mathf.Max(60, settings.requestTimeoutSeconds);
            byte[] bytes = await AssetGenHttp.GetBytesAsync(url, settings.apiKey, settings.baseUrl, timeout, ct);
            if (bytes == null || bytes.Length == 0) throw new IOException("3D 文件下载为空: " + url);
            if (!HasGlbMagic(bytes))
                throw new IOException("下载内容不是 GLB（前 4 字节 " + DescribeMagic(bytes) + "）: " + Truncate(url, 120));

            string directory = Path.Combine(Application.persistentDataPath, "EpicSpark", "generated");
            Directory.CreateDirectory(directory);
            string fileName = "gen_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture) + ".glb";
            string path = Path.Combine(directory, fileName);
            File.WriteAllBytes(path, bytes);
            Debug.Log("[资产生成] GLB 已保存：" + path + "（" + (bytes.Length / 1024) + " KB）");
            return path;
        }

        // ─────────────────────────── 响应解析 ───────────────────────────

        private static bool HasGlbMagic(byte[] bytes)
        {
            return bytes != null && bytes.Length >= 4
                && bytes[0] == (byte)'g' && bytes[1] == (byte)'l'
                && bytes[2] == (byte)'T' && bytes[3] == (byte)'F';
        }

        private static string DescribeMagic(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 4) return "(长度不足)";
            var builder = new StringBuilder(8);
            for (int i = 0; i < 4; i++)
            {
                byte b = bytes[i];
                builder.Append(b >= 32 && b < 127 ? ((char)b).ToString() : ("0x" + b.ToString("x2", CultureInfo.InvariantCulture)));
            }
            return builder.ToString();
        }

        private static string DecodeText(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return string.Empty;
            return Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
        }

        private static Dictionary<string, object> TryParseObject(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                return MiniJson.AsDict(MiniJson.Parse(json));
            }
            catch (FormatException e)
            {
                Debug.LogWarning("[资产生成] JSON 解析失败: " + e.Message + " | " + Truncate(json, 160));
                return null;
            }
        }

        /// <summary>从 JSON 文本里取图片：data[0].b64_json / data[0].url / image / images[] / 顶层 url。</summary>
        private static async Task<byte[]> ExtractImageFromJsonAsync(string json, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(json)) return null;
            Dictionary<string, object> root = TryParseObject(json.TrimStart());
            if (root != null)
            {
                List<object> data = MiniJson.GetList(root, "data");
                if (data != null && data.Count > 0)
                {
                    byte[] image = await DecodeImageNodeAsync(MiniJson.AsDict(data[0]), ct);
                    if (image != null) return image;
                }

                byte[] direct = await DecodeImageNodeAsync(root, ct);
                if (direct != null) return direct;

                List<object> images = MiniJson.GetList(root, "images");
                if (images != null && images.Count > 0 && images[0] is string first)
                {
                    byte[] decoded = AssetGenText.TryDecodeBase64(first);
                    if (decoded != null) return decoded;
                }
            }

            // 兜底：响应不是标准结构时，按字段名扫描（b64 或 url）。
            byte[] scanned = AssetGenText.ScanImageField(json);
            if (scanned != null) return scanned;
            string scannedUrl = AssetGenText.ScanUrlField(json);
            if (!string.IsNullOrEmpty(scannedUrl)) return await DownloadBytesAsync(scannedUrl, ct);
            return null;
        }

        /// <summary>抠图响应可能是 JSON，也可能是直接返回的 PNG/JPEG 二进制。</summary>
        private static async Task<byte[]> ExtractImageFromBytesAsync(byte[] response, CancellationToken ct)
        {
            if (response == null || response.Length == 0) return null;
            if (IsPng(response) || IsJpeg(response)) return response;
            string text = DecodeText(response);
            if (text.StartsWith("{")) return await ExtractImageFromJsonAsync(text, ct);
            if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return await DownloadBytesAsync(text.Trim(), ct);
            }
            return null;
        }

        private static async Task<byte[]> DecodeImageNodeAsync(Dictionary<string, object> node, CancellationToken ct)
        {
            if (node == null) return null;

            string[] base64Fields = { "b64_json", "base64", "image_base64", "image", "output", "result" };
            for (int i = 0; i < base64Fields.Length; i++)
            {
                object value;
                if (!MiniJson.TryGet(node, base64Fields[i], out value)) continue;
                string text = value as string;
                if (string.IsNullOrEmpty(text)) continue;
                byte[] decoded = AssetGenText.TryDecodeBase64(text);
                if (decoded != null && decoded.Length > 0) return decoded;
                if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    return await DownloadBytesAsync(text, ct);
                }
            }

            string[] urlFields = { "url", "image_url", "file_url", "output_url", "result_url" };
            for (int i = 0; i < urlFields.Length; i++)
            {
                string url = MiniJson.GetString(node, urlFields[i]);
                if (!string.IsNullOrEmpty(url)) return await DownloadBytesAsync(url, ct);
            }
            return null;
        }

        private static async Task<byte[]> DownloadBytesAsync(string url, CancellationToken ct)
        {
            // 图像下载不强依赖 Date 头，但复用同一传输层（统一走手写报文）。
            return await AssetGenHttp.GetBytesAsync(url, string.Empty, string.Empty, 180, ct);
        }

        private static string FindTaskId(Dictionary<string, object> root)
        {
            if (root == null) return null;
            string[] fields = { "task_id", "taskId", "id", "request_id", "requestId", "job_id", "jobId" };
            for (int i = 0; i < fields.Length; i++)
            {
                string value = MiniJson.GetString(root, fields[i]);
                if (!string.IsNullOrEmpty(value)) return value;
            }
            Dictionary<string, object> data = MiniJson.GetDict(root, "data");
            if (data != null)
            {
                for (int i = 0; i < fields.Length; i++)
                {
                    string value = MiniJson.GetString(data, fields[i]);
                    if (!string.IsNullOrEmpty(value)) return value;
                }
            }
            Dictionary<string, object> task = MiniJson.GetDict(root, "task");
            if (task != null)
            {
                string value = MiniJson.GetString(task, "id");
                if (!string.IsNullOrEmpty(value)) return value;
            }
            return null;
        }

        private static string FindStatus(Dictionary<string, object> root)
        {
            if (root == null) return string.Empty;
            string value = MiniJson.GetString(root, "status");
            if (string.IsNullOrEmpty(value)) value = MiniJson.GetString(root, "state");
            if (string.IsNullOrEmpty(value)) value = MiniJson.GetString(root, "task_status");
            if (string.IsNullOrEmpty(value))
            {
                Dictionary<string, object> data = MiniJson.GetDict(root, "data");
                if (data != null) value = MiniJson.GetString(data, "status");
            }
            return value ?? string.Empty;
        }

        private static string FindFileUrl(Dictionary<string, object> root)
        {
            if (root == null) return null;

            string[] nestedKeys = { "output", "result", "data", "file" };
            for (int i = 0; i < nestedKeys.Length; i++)
            {
                Dictionary<string, object> nested = MiniJson.GetDict(root, nestedKeys[i]);
                if (nested == null) continue;
                string url = FindFileUrlShallow(nested);
                if (!string.IsNullOrEmpty(url)) return url;
            }

            return FindFileUrlShallow(root);
        }

        private static string FindFileUrlShallow(Dictionary<string, object> node)
        {
            if (node == null) return null;
            string[] fields = { "file_url", "glb_url", "model_url", "output_url", "result_url", "url" };
            for (int i = 0; i < fields.Length; i++)
            {
                string value = MiniJson.GetString(node, fields[i]);
                if (!string.IsNullOrEmpty(value)
                    && (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                        || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                {
                    return value;
                }
            }
            return null;
        }

        private static bool IsFinishedStatus(string status)
        {
            if (string.IsNullOrEmpty(status)) return false;
            string lower = status.ToLowerInvariant();
            return lower.Contains("success") || lower.Contains("succeed") || lower.Contains("complet")
                || lower.Contains("done") || lower.Contains("finish") || lower == "ok";
        }

        private static bool IsFailureStatus(string status)
        {
            if (string.IsNullOrEmpty(status)) return false;
            string lower = status.ToLowerInvariant();
            return lower.Contains("fail") || lower.Contains("error") || lower.Contains("cancel") || lower.Contains("reject");
        }

        private static bool IsPng(byte[] bytes)
        {
            return bytes != null && bytes.Length > 8
                && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47;
        }

        private static bool IsJpeg(byte[] bytes)
        {
            return bytes != null && bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8;
        }

        // ─────────────────────────── 落盘 ───────────────────────────

        private static string SaveDebugImage(byte[] png, string tag)
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

        private static string Truncate(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            string single = text.Replace('\n', ' ').Replace('\r', ' ');
            return single.Length <= maxLength ? single : single.Substring(0, maxLength) + "…";
        }
    }

    // ─────────────────────────── JSON 字段扫描兜底 ───────────────────────────

    /// <summary>标准结构解析失败时的兜底：按字段名扫描原始 JSON 文本。</summary>
    internal static class AssetGenText
    {
        public static byte[] ScanImageField(string json)
        {
            string[] fields = { "b64_json", "base64", "image_base64", "image" };
            for (int i = 0; i < fields.Length; i++)
            {
                string value = ExtractString(json, fields[i]);
                if (string.IsNullOrEmpty(value)) continue;
                byte[] decoded = TryDecodeBase64(value);
                if (decoded != null && decoded.Length > 0) return decoded;
            }
            return null;
        }

        public static string ScanUrlField(string json)
        {
            string[] fields = { "url", "image_url", "file_url", "output_url", "result_url" };
            for (int i = 0; i < fields.Length; i++)
            {
                string value = ExtractString(json, fields[i]);
                if (string.IsNullOrEmpty(value)) continue;
                if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    return value;
                }
            }
            return null;
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

        public static byte[] TryDecodeBase64(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            string data = value.Trim();
            int comma = data.IndexOf(',');
            if (data.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0)
                data = data.Substring(comma + 1);
            data = data.Replace("\r", string.Empty).Replace("\n", string.Empty).Replace(" ", string.Empty);
            if (data.Length == 0) return null;
            try
            {
                return Convert.FromBase64String(data);
            }
            catch (FormatException)
            {
                return null;
            }
        }
    }
}
