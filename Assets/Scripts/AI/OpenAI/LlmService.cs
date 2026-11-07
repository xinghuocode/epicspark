// ILlmService 的 OpenAI 兼容实现。全部 Agent 共用同一个实例，只换 systemPrompt。
// 没有 API Key 时 HasRemote=false，三个方法立即返回 null，绝不发请求、绝不抛异常。
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EpicSpark.Core;
using UnityEngine;

namespace EpicSpark.AI.OpenAI
{
    /// <summary>
    /// 推理 LLM 服务。失败（网络 / 解析 / 超时 / 取消）统一返回 null 或带 error 的响应，
    /// 由调用方（Story / Graybox / Build / AssetGen）降级到确定性本地逻辑。
    /// </summary>
    public class LlmService : ILlmService
    {
        public const string DefaultChatModel = "Qwen3-32B";
        public const string DefaultEmbedModel = "Qwen3-Embedding-8B";
        private const string DefaultBaseUrl = "https://ai.gitee.com/v1";

        private OpenAIClient client;

        /// <summary>无参构造：不带 Key，HasRemote=false，全链路走确定性兜底。</summary>
        public LlmService()
            : this((OpenAISettings)null)
        {
        }

        /// <summary>从 ScriptableObject 配置构造（复制其字段，之后修改 SO 不影响已建实例）。</summary>
        public LlmService(OpenAISettings settings)
        {
            client = new OpenAIClient(settings);
        }

        /// <summary>直接给参数的构造，便于 Bootstrap 用环境变量 / PlayerPrefs 装配。</summary>
        public LlmService(
            string baseUrl,
            string apiKey,
            string chatModel = DefaultChatModel,
            string embedModel = DefaultEmbedModel,
            int timeoutSeconds = 60,
            int maxRetries = 3,
            bool offline = false)
        {
            client = new OpenAIClient(
                string.IsNullOrEmpty(baseUrl) ? DefaultBaseUrl : baseUrl,
                apiKey,
                chatModel,
                embedModel,
                timeoutSeconds,
                maxRetries,
                0.85f,
                offline,
                true);
        }

        /// <summary>从 Resources 目录读配置（可选）。找不到时返回无 Key 的默认实例。</summary>
        public static LlmService FromResources(string resourcePath = "EpicSpark/OpenAISettings")
        {
            try
            {
                OpenAISettings settings = Resources.Load<OpenAISettings>(resourcePath);
                return new LlmService(settings);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Llm] 读取 OpenAI 配置失败: " + e.Message);
                return new LlmService();
            }
        }

        public OpenAIClient Client { get { return client; } }

        /// <summary>
        /// 热更新端点 / Key / 模型（设置界面保存后调用）。进行中的请求继续用旧客户端，
        /// 之后的请求立刻使用新配置；没有 Key 时 HasRemote 会立即变为 false。
        /// </summary>
        public void Reconfigure(OpenAISettings settings)
        {
            client = new OpenAIClient(settings);
            Debug.Log("[Llm] 配置已更新: 端点=" + BaseUrl + " 对话模型=" + ChatModel
                      + " 向量模型=" + EmbedModel + " 远端可用=" + HasRemote);
        }

        public string BaseUrl { get { return client != null ? client.BaseUrl : ""; } }
        public string ChatModel { get { return client != null ? client.ChatModel : DefaultChatModel; } }
        public string EmbedModel { get { return client != null ? client.EmbedModel : DefaultEmbedModel; } }

        /// <summary>最近一次失败的简短原因（诊断用，不参与逻辑）。</summary>
        public string LastError { get { return client != null ? client.LastError : "未初始化"; } }

        /// <summary>EmbedAsync 是否把向量归一化（默认 true，便于直接做余弦相似度）。</summary>
        public bool NormalizeEmbeddings { get; set; } = true;

        /// <summary>强制离线开关；打开后与没有 Key 等价。</summary>
        public bool Offline
        {
            get { return client == null || client.Offline; }
            set { if (client != null) client.Offline = value; }
        }

        public bool HasRemote
        {
            get { return client != null && client.CanReachRemote; }
        }

        // ─────────────────────────── ILlmService ───────────────────────────

        public async Task<string> CompleteAsync(
            string systemPrompt,
            string userPrompt,
            float temperature,
            CancellationToken ct)
        {
            try
            {
                if (!HasRemote) return null;
                if (ct.IsCancellationRequested) return null;
                return await client.ChatContentAsync(systemPrompt, userPrompt, temperature, ct);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Llm] CompleteAsync 异常，降级到本地逻辑: " + e.Message);
                return null;
            }
        }

        public async Task<LlmToolResponse> CompleteWithToolsAsync(
            string systemPrompt,
            string userPrompt,
            IReadOnlyList<LlmToolDef> tools,
            float temperature,
            CancellationToken ct)
        {
            if (!HasRemote)
            {
                return new LlmToolResponse { error = "未配置 API Key 或已离线" };
            }
            try
            {
                if (ct.IsCancellationRequested)
                {
                    return new LlmToolResponse { error = "已取消" };
                }
                return await client.ChatWithToolsAsync(systemPrompt, userPrompt, tools, temperature, ct);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Llm] CompleteWithToolsAsync 异常，降级到本地逻辑: " + e.Message);
                return new LlmToolResponse { error = e.GetType().Name + ": " + e.Message };
            }
        }

        public async Task<float[]> EmbedAsync(string text, CancellationToken ct)
        {
            try
            {
                if (!HasRemote) return null;
                if (ct.IsCancellationRequested) return null;
                float[] vector = await client.EmbedAsync(text, ct);
                if (vector == null) return null;
                return NormalizeEmbeddings ? Normalize(vector) : vector;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Llm] EmbedAsync 异常，降级到本地向量: " + e.Message);
                return null;
            }
        }

        // ─────────────────────────── 工具 ───────────────────────────

        private static float[] Normalize(float[] vector)
        {
            double sum = 0d;
            for (int i = 0; i < vector.Length; i++)
            {
                double v = vector[i];
                sum += v * v;
            }
            if (sum <= 1e-12d) return vector;
            float length = (float)Math.Sqrt(sum);
            var result = new float[vector.Length];
            for (int i = 0; i < vector.Length; i++) result[i] = vector[i] / length;
            return result;
        }
    }
}
