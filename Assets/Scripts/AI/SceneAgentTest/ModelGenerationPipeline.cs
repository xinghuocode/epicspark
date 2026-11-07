using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EpicSpark.AI.OpenAI;
using UnityEngine;

namespace EpicSpark.AI.SceneAgentTest
{
    /// <summary>
    /// 模型生成管线 (ARCHITECT.md §4.4): Z-Image-Turbo 文生图 → RMBG-2.0 去背景 → Hunyuan3D-2 图生3D。
    /// 全部调用 ai.gitee.com 的 OpenAI 兼容 / Serverless API。
    /// 词嵌入向量缓存比对(阈值复用)暂未接入 —— 当前由剧情/场景Agent的策略决定是否调用。
    /// </summary>
    public class ModelGenerationPipeline
    {
        // ---- gitee AI 端点 (相对于 baseUrl, 即 https://ai.gitee.com/v1) ----
        public const string Text2ImagePath = "/images/generations";        // Z-Image-Turbo
        public const string RemoveBgPath = "/images/mattings";             // RMBG-2.0 抠图 (官方 OpenAPI: 图像生成/抠图)
        public const string ImageTo3dPath = "/async/image-to-3d";          // Hunyuan3D-2 (异步)
        public const string TaskPath = "/task/";                           // 异步任务轮询

        const int PollIntervalSeconds = 5;
        const int PollTimeoutSeconds = 15 * 60; // 3D 生成较慢, 最多等 15 分钟

        readonly OpenAIClient client;

        public ModelGenerationPipeline(OpenAIClient client)
        {
            this.client = client ?? throw new ArgumentNullException(nameof(client));
        }

        /// <summary>完整管线: 返回 GLB 文件字节。onProgress 汇报各阶段。</summary>
        public async Task<byte[]> GenerateGlbAsync(string prompt, Action<string> onProgress, CancellationToken cancellationToken)
        {
            onProgress?.Invoke("文生图 (Z-Image-Turbo) ...");
            byte[] image = await TextToImageAsync(prompt, cancellationToken);

            onProgress?.Invoke("去背景 (RMBG-2.0) ...");
            byte[] cutout = await RemoveBackgroundAsync(image, cancellationToken);
            if (cutout == null)
            {
                onProgress?.Invoke("去背景失败, 使用原图继续");
                cutout = image;
            }

            onProgress?.Invoke("图生3D (Hunyuan3D-2 异步任务) ...");
            return await ImageTo3dAsync(cutout, cancellationToken);
        }

        // ---------- 1. Z-Image-Turbo 文生图 ----------

        async Task<byte[]> TextToImageAsync(string prompt, CancellationToken ct)
        {
            string body = MiniJson.Serialize(new Dictionary<string, object>
            {
                ["model"] = "Z-Image-Turbo",
                ["prompt"] = prompt,
                ["size"] = "1024x1024",
                ["num_inference_steps"] = 8,
                ["response_format"] = "b64_json",
            });
            string resp = await client.PostJsonAsync(Text2ImagePath, body, ct);
            var root = MiniJson.AsDict(MiniJson.Parse(resp));
            var data = MiniJson.GetList(root, "data");
            if (data == null || data.Count == 0)
                throw new InvalidOperationException("文生图响应无 data: " + Shorten(resp));

            var item = MiniJson.AsDict(data[0]);
            string b64 = MiniJson.GetString(item, "b64_json");
            if (!string.IsNullOrEmpty(b64))
                return Convert.FromBase64String(StripBase64(b64));

            string url = MiniJson.GetString(item, "url");
            if (!string.IsNullOrEmpty(url))
                return await client.GetBytesAsync(url, ct);

            throw new InvalidOperationException("文生图响应既无 b64_json 也无 url");
        }

        // ---------- 2. RMBG-2.0 去背景 ----------

        /// <summary>返回透明背景 PNG; 失败返回 null(调用方回退原图, Hunyuan3D 内部也会再做 rembg)。</summary>
        async Task<byte[]> RemoveBackgroundAsync(byte[] imagePng, CancellationToken ct)
        {
            try
            {
                var fields = new Dictionary<string, string>
                {
                    ["model"] = "RMBG-2.0",
                    ["response_format"] = "b64_json",
                };
                byte[] resp = await client.PostMultipartAsync(RemoveBgPath, fields, imagePng, "image", "image.png", "image/png", ct);
                if (TryExtractImage(resp, out var png)) return png;

                // 部分接口返回 url 而非 b64_json
                var root = MiniJson.AsDict(MiniJson.Parse(System.Text.Encoding.UTF8.GetString(resp).TrimStart()));
                var data = MiniJson.GetList(root, "data");
                string url = data != null && data.Count > 0 ? MiniJson.GetString(MiniJson.AsDict(data[0]), "url") : null;
                if (!string.IsNullOrEmpty(url))
                {
                    var downloaded = await DownloadImageAsync(url, ct);
                    if (downloaded != null && downloaded.Length > 0) return downloaded;
                }

                Debug.LogWarning("[ModelGen] 去背景响应无法解析, 回退原图: " + Shorten(System.Text.Encoding.UTF8.GetString(resp)));
                return null;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ModelGen] 去背景调用失败, 回退原图: " + e.Message);
                return null;
            }
        }

        // ---------- 3. Hunyuan3D-2 图生3D (异步) ----------

        async Task<byte[]> ImageTo3dAsync(byte[] imagePng, CancellationToken ct)
        {
            var fields = new Dictionary<string, string>
            {
                ["type"] = "glb",
                ["model"] = "Hunyuan3D-2",
                ["texture"] = "true",
                ["seed"] = "1234",
                ["num_inference_steps"] = "5",
                ["octree_resolution"] = "128",
                ["guidance_scale"] = "5",
            };
            byte[] submitBytes = await client.PostMultipartAsync(ImageTo3dPath, fields, imagePng, "image", "image.png", "image/png", ct);
            string submit = System.Text.Encoding.UTF8.GetString(submitBytes);
            var submitRoot = MiniJson.AsDict(MiniJson.Parse(submit));
            string taskId = MiniJson.GetString(submitRoot, "task_id");
            if (string.IsNullOrEmpty(taskId))
                throw new InvalidOperationException("图生3D 未返回 task_id: " + Shorten(submit));

            // 轮询任务
            int elapsed = 0;
            while (elapsed < PollTimeoutSeconds)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(PollIntervalSeconds * 1000, ct);
                elapsed += PollIntervalSeconds;

                string statusBody = await client.GetStringAsync(TaskPath + Uri.EscapeDataString(taskId), ct);
                var status = MiniJson.AsDict(MiniJson.Parse(statusBody));
                string state = MiniJson.GetString(status, "status", "unknown");
                if (state == "success")
                {
                    var output = MiniJson.GetDict(status, "output");
                    string fileUrl = MiniJson.GetString(output, "file_url");
                    if (string.IsNullOrEmpty(fileUrl))
                        throw new InvalidOperationException("任务成功但无 file_url: " + Shorten(statusBody));
                    return await client.GetBytesAsync(fileUrl, ct);
                }
                if (state == "failed" || state == "cancelled")
                    throw new InvalidOperationException($"图生3D 任务 {state}: " + Shorten(statusBody));
            }
            throw new TimeoutException("图生3D 任务超时(" + PollTimeoutSeconds + "s)");
        }

        // ---------- 辅助 ----------

        /// <summary>解析图像处理响应: 支持 {data:[{b64_json|url}]}, {"image": "<b64或url>"}, 或直接二进制图像。</summary>
        static bool TryExtractImage(byte[] resp, out byte[] image)
        {
            image = null;
            if (resp == null || resp.Length == 0) return false;

            // 直接二进制图像 (png/jpg 魔数)
            if (resp[0] == 0x89 && resp[1] == 0x50) { image = resp; return true; } // PNG
            if (resp[0] == 0xFF && resp[1] == 0xD8) { image = resp; return true; } // JPEG

            string text = System.Text.Encoding.UTF8.GetString(resp).TrimStart();
            if (!text.StartsWith("{")) return false;
            var root = MiniJson.AsDict(MiniJson.Parse(text));

            var data = MiniJson.GetList(root, "data");
            if (data != null && data.Count > 0)
            {
                var item = MiniJson.AsDict(data[0]);
                string b64 = MiniJson.GetString(item, "b64_json");
                if (!string.IsNullOrEmpty(b64)) { image = Convert.FromBase64String(StripBase64(b64)); return true; }
            }
            return false;
        }

        /// <summary>从 URL 下载图像(去背景结果为 url 格式时)。</summary>
        async Task<byte[]> DownloadImageAsync(string url, CancellationToken ct) => await client.GetBytesAsync(url, ct);

        static string StripBase64(string b64)
        {
            var sb = new System.Text.StringBuilder(b64.Length);
            foreach (char c in b64)
                if (!char.IsWhiteSpace(c)) sb.Append(c);
            return sb.ToString();
        }

        static string Shorten(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\n", " ");
            return s.Length > 200 ? s.Substring(0, 200) + "…" : s;
        }
    }
}
