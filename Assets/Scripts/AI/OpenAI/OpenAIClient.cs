// OpenAI 兼容 HTTP 客户端：chat/completions（含 tools）与 embeddings。
// 只用 UnityWebRequest + JsonUtility；失败重试 3 次，指数退避；取消时 Abort。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EpicSpark.Core;
using UnityEngine;
using UnityEngine.Networking;

namespace EpicSpark.AI.OpenAI
{
    /// <summary>
    /// 低层 HTTP 客户端。不实现 ILlmService，只负责把请求打出去、把响应解析成文本 / 工具调用 / 向量。
    /// 任何网络或解析失败都通过返回值 / <see cref="LastError"/> 暴露，不向上抛。
    /// </summary>
    public sealed class OpenAIClient
    {
        private const string ChatPath = "/chat/completions";
        private const string EmbeddingPath = "/embeddings";

        private readonly string baseUrl;
        private readonly string apiKey;
        private readonly string chatModel;
        private readonly string embedModel;
        private readonly int timeoutSeconds;
        private readonly int maxRetries;
        private readonly int maxTokens;
        private readonly float defaultTemperature;
        private readonly bool verboseLogging;

        public OpenAIClient(OpenAISettings settings)
            : this(
                settings != null ? settings.ResolvedBaseUrl : string.Empty,
                settings != null ? settings.apiKey : string.Empty,
                settings != null ? settings.chatModel : LlmService.DefaultChatModel,
                settings != null ? settings.embedModel : LlmService.DefaultEmbedModel,
                settings != null ? settings.timeoutSeconds : 60,
                settings != null ? settings.maxRetries : 3,
                settings != null ? settings.defaultTemperature : 0.85f,
                settings != null && settings.offline,
                settings == null || settings.verboseLogging,
                settings != null ? settings.maxTokens : 2048)
        {
        }

        public OpenAIClient(
            string baseUrl,
            string apiKey,
            string chatModel,
            string embedModel,
            int timeoutSeconds = 60,
            int maxRetries = 3,
            float defaultTemperature = 0.85f,
            bool offline = false,
            bool verboseLogging = true,
            int maxTokens = 2048)
        {
            this.baseUrl = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
            this.apiKey = apiKey ?? string.Empty;
            this.chatModel = string.IsNullOrEmpty(chatModel) ? LlmService.DefaultChatModel : chatModel;
            this.embedModel = string.IsNullOrEmpty(embedModel) ? LlmService.DefaultEmbedModel : embedModel;
            this.timeoutSeconds = Mathf.Clamp(timeoutSeconds, 5, 600);
            this.maxRetries = Mathf.Clamp(maxRetries, 1, 6);
            this.maxTokens = Mathf.Clamp(maxTokens, 128, 32768);
            this.defaultTemperature = defaultTemperature;
            this.verboseLogging = verboseLogging;
            Offline = offline;
        }

        public string BaseUrl { get { return baseUrl; } }
        public string ChatModel { get { return chatModel; } }
        public string EmbedModel { get { return embedModel; } }
        public string ApiKey { get { return apiKey; } }
        public int MaxTokens { get { return maxTokens; } }
        public int TimeoutSeconds { get { return timeoutSeconds; } }
        public float DefaultTemperature { get { return defaultTemperature; } }
        public bool Offline { get; set; }
        public string LastError { get; private set; }

        /// <summary>有 Key 且配了 baseUrl 才算可用（没有 Key 时上层必须走确定性兜底）。</summary>
        public bool HasCredentials
        {
            get { return !string.IsNullOrEmpty(apiKey) && !string.IsNullOrEmpty(baseUrl); }
        }

        /// <summary>是否真的会发请求。</summary>
        public bool CanReachRemote
        {
            get { return HasCredentials && !Offline; }
        }

        // ─────────────────────────── 对话 ───────────────────────────

        /// <summary>最近一次对话的原始输出（调试录制用：与 LastReasoning 一起落盘）。</summary>
        public static string LastContent = "";
        public static string LastReasoning = "";

        /// <summary>单轮补全，返回 choices[0].message.content。失败返回 null。</summary>
        public async Task<string> ChatContentAsync(
            string systemPrompt,
            string userPrompt,
            float temperature,
            CancellationToken ct)
        {
            try
            {
                if (!CanReachRemote)
                {
                    LastError = "未配置 API Key 或已离线";
                    return null;
                }
                if (ct.IsCancellationRequested) return null;

                string body = BuildChatBody(chatModel, systemPrompt, userPrompt, ResolveTemperature(temperature), null, maxTokens);
                HttpOutcome outcome = await PostWithRetryAsync(baseUrl + ChatPath, body, ct);
                if (!outcome.ok)
                {
                    LastError = outcome.error;
                    return null;
                }

                OpenAIChatResponse parsed = ParseChatResponse(outcome.text, out string parseError);
                if (parsed == null)
                {
                    LastError = parseError;
                    return null;
                }
                if (parsed.choices == null || parsed.choices.Length == 0 || parsed.choices[0] == null ||
                    parsed.choices[0].message == null)
                {
                    LastError = "响应缺少 choices[0].message";
                    return null;
                }
                LastError = "";
                string content = parsed.choices[0].message.content;
                LastReasoning = parsed.choices[0].message.reasoning_content ?? "";
                LastContent = content ?? "";
                if (string.IsNullOrEmpty(content))
                {
                    // 推理型模型会把 max_tokens 花在 reasoning 上，正文为空
                    string reason = parsed.choices[0].finish_reason ?? "";
                    string reasoning = parsed.choices[0].message.reasoning_content;
                    LastError = "正文为空（finish_reason=" + reason
                                + (string.IsNullOrEmpty(reasoning) ? "" : "，有 " + reasoning.Length + " 字思维链")
                                + "），若是推理模型请调大 max_tokens（当前 " + maxTokens + "）";
                    LogWarning(LastError);
                    return null;
                }
                return content.Trim();
            }
            catch (Exception e)
            {
                LastError = e.GetType().Name + ": " + e.Message;
                LogWarning("对话请求异常: " + LastError);
                return null;
            }
        }

        /// <summary>带 tools 的补全，解析 tool_calls，同时保留普通 content。失败返回带 error 的响应。</summary>
        public async Task<LlmToolResponse> ChatWithToolsAsync(
            string systemPrompt,
            string userPrompt,
            IReadOnlyList<LlmToolDef> tools,
            float temperature,
            CancellationToken ct)
        {
            var response = new LlmToolResponse();
            try
            {
                if (!CanReachRemote)
                {
                    response.error = "未配置 API Key 或已离线";
                    LastError = response.error;
                    return response;
                }
                if (ct.IsCancellationRequested)
                {
                    response.error = "已取消";
                    return response;
                }

                string body = BuildChatBody(chatModel, systemPrompt, userPrompt, ResolveTemperature(temperature), tools, maxTokens);
                HttpOutcome outcome = await PostWithRetryAsync(baseUrl + ChatPath, body, ct);
                if (!outcome.ok)
                {
                    response.error = outcome.error;
                    LastError = outcome.error;
                    return response;
                }

                OpenAIChatResponse parsed = ParseChatResponse(outcome.text, out string parseError);
                if (parsed == null)
                {
                    response.error = parseError;
                    LastError = parseError;
                    return response;
                }
                if (parsed.choices == null || parsed.choices.Length == 0 || parsed.choices[0] == null)
                {
                    response.error = "响应缺少 choices";
                    LastError = response.error;
                    return response;
                }

                OpenAIMessage message = parsed.choices[0].message;
                if (message == null)
                {
                    response.error = "响应缺少 message";
                    LastError = response.error;
                    return response;
                }

                response.content = message.content ?? "";
                if (message.tool_calls != null)
                {
                    for (int i = 0; i < message.tool_calls.Length; i++)
                    {
                        OpenAIToolCall call = message.tool_calls[i];
                        if (call == null || call.function == null) continue;
                        response.calls.Add(new LlmToolCall
                        {
                            id = call.id ?? "",
                            name = call.function.name ?? "",
                            argumentsJson = call.function.arguments ?? "",
                        });
                    }
                }

                if (string.IsNullOrEmpty(response.content) && response.calls.Count == 0)
                {
                    string reason = parsed.choices[0].finish_reason ?? "";
                    string reasoning = message.reasoning_content;
                    response.error = "模型既没有返回内容也没有工具调用（finish_reason=" + reason
                                     + (string.IsNullOrEmpty(reasoning) ? "" : "，有 " + reasoning.Length + " 字思维链")
                                     + "）；推理模型请调大 max_tokens（当前 " + maxTokens + "）";
                    LastError = response.error;
                    return response;
                }

                LastError = "";
                return response;
            }
            catch (Exception e)
            {
                response.error = e.GetType().Name + ": " + e.Message;
                LastError = response.error;
                LogWarning("工具调用请求异常: " + response.error);
                return response;
            }
        }

        // ─────────────────────────── 词嵌入 ───────────────────────────

        /// <summary>返回 data[0].embedding（原始数值，不做归一化）。失败返回 null。</summary>
        public async Task<float[]> EmbedAsync(string text, CancellationToken ct)
        {
            try
            {
                if (!CanReachRemote)
                {
                    LastError = "未配置 API Key 或已离线";
                    return null;
                }
                if (string.IsNullOrEmpty(text))
                {
                    LastError = "嵌入输入为空";
                    return null;
                }
                if (ct.IsCancellationRequested) return null;

                var sb = new StringBuilder(text.Length + 96);
                sb.Append("{\"model\":").Append(OpenAIText.Quote(embedModel));
                sb.Append(",\"input\":").Append(OpenAIText.Quote(text));
                sb.Append(",\"encoding_format\":\"float\"}");

                HttpOutcome outcome = await PostWithRetryAsync(baseUrl + EmbeddingPath, sb.ToString(), ct);
                if (!outcome.ok)
                {
                    LastError = outcome.error;
                    return null;
                }

                OpenAIEmbeddingResponse parsed = null;
                try { parsed = JsonUtility.FromJson<OpenAIEmbeddingResponse>(outcome.text); }
                catch (Exception e) { LastError = "嵌入响应解析失败: " + e.Message; return null; }

                if (parsed == null)
                {
                    LastError = "嵌入响应不是合法 JSON";
                    return null;
                }
                if (parsed.error != null && !string.IsNullOrEmpty(parsed.error.message))
                {
                    LastError = "远端错误: " + parsed.error.message;
                    return null;
                }
                if (parsed.data == null || parsed.data.Length == 0 || parsed.data[0] == null ||
                    parsed.data[0].embedding == null || parsed.data[0].embedding.Length == 0)
                {
                    LastError = "嵌入响应 data[0].embedding 为空";
                    return null;
                }

                LastError = "";
                return parsed.data[0].embedding;
            }
            catch (Exception e)
            {
                LastError = e.GetType().Name + ": " + e.Message;
                LogWarning("嵌入请求异常: " + LastError);
                return null;
            }
        }

        // ─────────────────────────── 请求体构造 ───────────────────────────

        /// <summary>拼 chat/completions 请求体；tools 为 null/空时不带 tools 字段。</summary>
        public static string BuildChatBody(
            string model,
            string systemPrompt,
            string userPrompt,
            float temperature,
            IReadOnlyList<LlmToolDef> tools,
            int maxTokens = 0)
        {
            var sb = new StringBuilder(1024);
            sb.Append("{\"model\":").Append(OpenAIText.Quote(model));
            sb.Append(",\"messages\":[");
            sb.Append("{\"role\":\"system\",\"content\":").Append(OpenAIText.Quote(systemPrompt ?? "")).Append('}');
            sb.Append(",{\"role\":\"user\",\"content\":").Append(OpenAIText.Quote(userPrompt ?? "")).Append('}');
            sb.Append(']');
            sb.Append(",\"temperature\":").Append(temperature.ToString("0.###", CultureInfo.InvariantCulture));
            sb.Append(",\"stream\":false");
            if (maxTokens > 0) sb.Append(",\"max_tokens\":").Append(maxTokens);

            if (tools != null && tools.Count > 0)
            {
                sb.Append(",\"tools\":[");
                for (int i = 0; i < tools.Count; i++)
                {
                    LlmToolDef def = tools[i];
                    if (def == null) continue;
                    if (i > 0) sb.Append(',');
                    string schema = string.IsNullOrEmpty(def.parametersJson)
                        ? "{\"type\":\"object\",\"properties\":{}}"
                        : def.parametersJson.Trim();
                    sb.Append("{\"type\":\"function\",\"function\":{");
                    sb.Append("\"name\":").Append(OpenAIText.Quote(def.name));
                    sb.Append(",\"description\":").Append(OpenAIText.Quote(def.description));
                    sb.Append(",\"parameters\":").Append(schema);
                    sb.Append("}}");
                }
                sb.Append(']');
                sb.Append(",\"tool_choice\":\"auto\"");
            }

            sb.Append('}');
            return sb.ToString();
        }

        private float ResolveTemperature(float temperature)
        {
            if (temperature <= 0f) return defaultTemperature;
            return Mathf.Clamp(temperature, 0f, 2f);
        }

        private static OpenAIChatResponse ParseChatResponse(string text, out string error)
        {
            error = "";
            if (string.IsNullOrEmpty(text))
            {
                error = "响应为空";
                return null;
            }
            OpenAIChatResponse parsed = null;
            try { parsed = JsonUtility.FromJson<OpenAIChatResponse>(text); }
            catch (Exception e)
            {
                error = "响应解析失败: " + e.Message;
                return null;
            }
            if (parsed == null)
            {
                error = "响应不是合法 JSON";
                return null;
            }
            if (parsed.error != null && !string.IsNullOrEmpty(parsed.error.message))
            {
                error = "远端错误: " + parsed.error.message;
                return null;
            }
            return parsed;
        }

        // ─────────────────────────── HTTP ───────────────────────────

        private sealed class HttpOutcome
        {
            public bool ok;
            public bool canceled;
            public int status;
            public string text = "";
            public string error = "";
        }

        private static bool IsRetryable(int status)
        {
            return status == 0 || status == 408 || status == 409 || status == 425 || status == 429 || status >= 500;
        }

        private async Task<HttpOutcome> PostWithRetryAsync(string url, string json, CancellationToken ct)
        {
            HttpOutcome last = null;
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                if (ct.IsCancellationRequested)
                {
                    return new HttpOutcome { canceled = true, error = "已取消" };
                }

                HttpOutcome outcome = await PostOnceAsync(url, json, ct);
                if (outcome.ok) return outcome;
                last = outcome;
                if (outcome.canceled) return outcome;
                if (!IsRetryable(outcome.status)) return outcome;
                if (attempt >= maxRetries) return outcome;

                int delayMs = 400 * (1 << (attempt - 1));
                try { await Task.Delay(delayMs, ct); }
                catch (OperationCanceledException) { return new HttpOutcome { canceled = true, error = "已取消" }; }
            }
            return last ?? new HttpOutcome { error = "请求失败" };
        }

        private async Task<HttpOutcome> PostOnceAsync(string url, string json, CancellationToken ct)
        {
            var outcome = new HttpOutcome();
            try
            {
                using (var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
                {
                    byte[] payload = Encoding.UTF8.GetBytes(json ?? string.Empty);
                    request.uploadHandler = new UploadHandlerRaw(payload);
                    request.downloadHandler = new DownloadHandlerBuffer();
                    request.timeout = timeoutSeconds;
                    request.SetRequestHeader("Content-Type", "application/json");
                    request.SetRequestHeader("Accept", "application/json");
                    if (!string.IsNullOrEmpty(apiKey))
                        request.SetRequestHeader("Authorization", "Bearer " + apiKey);

                    UnityWebRequestAsyncOperation operation;
                    try { operation = request.SendWebRequest(); }
                    catch (Exception e)
                    {
                        outcome.error = "发送请求失败: " + e.Message;
                        return outcome;
                    }

                    while (!operation.isDone)
                    {
                        if (ct.IsCancellationRequested)
                        {
                            request.Abort();
                            outcome.canceled = true;
                            outcome.error = "已取消";
                            return outcome;
                        }
                        await Task.Yield();
                    }

                    outcome.status = (int)request.responseCode;
                    outcome.text = request.downloadHandler != null ? request.downloadHandler.text : string.Empty;

                    if (request.result != UnityWebRequest.Result.Success)
                    {
                        string detail = string.IsNullOrEmpty(outcome.text)
                            ? (request.error ?? "")
                            : OpenAIText.TrimForLog(outcome.text, 320);
                        outcome.error = "HTTP " + outcome.status + " " + (request.error ?? "") + " | " + detail;
                        LogWarning("请求失败 " + url + " -> " + outcome.error);
                        return outcome;
                    }

                    outcome.ok = true;
                    return outcome;
                }
            }
            catch (Exception e)
            {
                outcome.error = e.GetType().Name + ": " + e.Message;
                LogWarning("请求异常 " + url + " -> " + outcome.error);
                return outcome;
            }
        }

        private void LogWarning(string message)
        {
            if (!verboseLogging) return;
            Debug.LogWarning("[Llm] " + message);
        }
    }
}
