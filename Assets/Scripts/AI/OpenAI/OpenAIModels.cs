using System;
using System.Collections.Generic;
using System.Text;

namespace EpicSpark.AI.OpenAI
{
    // ============ 请求侧 ============

    /// <summary>聊天消息。role 取值: system / user / assistant / tool。</summary>
    [Serializable]
    public class ChatMessage
    {
        public string role;
        public string content;
        /// <summary>assistant 消息携带的工具调用(仅响应侧反序列化/请求侧回传时使用)。</summary>
        public ToolCall[] tool_calls;
        /// <summary>role 为 "tool" 的消息必须携带对应 tool_calls 元素的 id。</summary>
        public string tool_call_id;

        public static ChatMessage System(string content) => new ChatMessage { role = "system", content = content };
        public static ChatMessage User(string content) => new ChatMessage { role = "user", content = content };
        public static ChatMessage Assistant(string content) => new ChatMessage { role = "assistant", content = content };

        /// <summary>构造 tool 角色的工具结果消息。</summary>
        public static ChatMessage Tool(string toolCallId, string content) =>
            new ChatMessage { role = "tool", tool_call_id = toolCallId, content = content };
    }

    /// <summary>聊天请求的可选参数。未设置的字段不会出现在请求体中。</summary>
    public class ChatOptions
    {
        public float? temperature;
        public int? maxTokens;
        public bool stream;
        /// <summary>Function Calling 工具列表(原生 tool call)。设置后服务端可返回 message.tool_calls。</summary>
        public ToolDefinition[] tools;
        /// <summary>工具选择策略, 例如 "auto" / "required" / "none"。默认不发送。</summary>
        public string toolChoice;
        // TODO: 后续按需扩展: top_p, stop, response_format, parallel_tool_calls, seed 等
    }

    // ============ Function Calling (Tools) ============

    /// <summary>工具定义(请求侧)。parameters 为 JSON Schema 原文。</summary>
    [Serializable]
    public class ToolDefinition
    {
        public string type = "function";
        public ToolFunctionInfo function;
    }

    [Serializable]
    public class ToolFunctionInfo
    {
        public string name;
        public string description;
        /// <summary>参数 JSON Schema 原文(不转义地嵌入请求体)。</summary>
        public string parameters;
    }

    /// <summary>assistant 消息中的工具调用(响应侧)。arguments 为 JSON 字符串, 需自行解析。</summary>
    [Serializable]
    public class ToolCall
    {
        public string id;
        public string type;
        public ToolCallFunction function;
    }

    [Serializable]
    public class ToolCallFunction
    {
        public string name;
        public string arguments;
    }

    // ============ 响应侧 DTO (供 JsonUtility 反序列化) ============

    [Serializable]
    public class ChatResponse
    {
        public string id;
        public long created; // Unix 秒, JSON 中是数字
        public string model;
        public ChatChoice[] choices;
        public ChatUsage usage;
    }

    [Serializable]
    public class ChatChoice
    {
        public int index;
        public ChatMessage message;
        public string finish_reason;
    }

    [Serializable]
    public class ChatUsage
    {
        public int prompt_tokens;
        public int completion_tokens;
        public int total_tokens;
    }

    [Serializable]
    public class ChatStreamChunk
    {
        public string id;
        public string model;
        public ChatStreamChoice[] choices;
    }

    [Serializable]
    public class ChatStreamChoice
    {
        public int index;
        public ChatStreamDelta delta;
        public string finish_reason;
    }

    [Serializable]
    public class ChatStreamDelta
    {
        public string role;
        public string content;
    }

    [Serializable]
    public class ModelListResponse
    {
        public ModelInfo[] data;
    }

    [Serializable]
    public class ModelInfo
    {
        public string id;
        public string owned_by;
    }

    [Serializable]
    class EmbeddingRequest
    {
        public string model;
        public string input;
    }

    [Serializable]
    public class EmbeddingResponse
    {
        public EmbeddingData[] data;
        public ChatUsage usage;
    }

    [Serializable]
    public class EmbeddingData
    {
        public int index;
        public float[] embedding;
    }

    // ============ 请求 JSON 构造 ============

    /// <summary>
    /// 手工构造聊天请求 JSON, 以便省略未设置的可选字段。
    /// (JsonUtility 无法表达"可选字段", 也无法序列化顶层 Dictionary。)
    /// </summary>
    internal static class ChatRequestJson
    {
        public static string Build(string model, IReadOnlyList<ChatMessage> messages, ChatOptions options)
        {
            var sb = new StringBuilder(256);
            sb.Append("{\"model\":").Append(Escape(model));
            sb.Append(",\"messages\":[");
            for (int i = 0; i < messages.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var msg = messages[i];
                sb.Append("{\"role\":").Append(Escape(msg.role));
                sb.Append(",\"content\":").Append(Escape(msg.content ?? ""));
                if (!string.IsNullOrEmpty(msg.tool_call_id))
                    sb.Append(",\"tool_call_id\":").Append(Escape(msg.tool_call_id));
                if (msg.tool_calls != null && msg.tool_calls.Length > 0)
                {
                    sb.Append(",\"tool_calls\":[");
                    for (int j = 0; j < msg.tool_calls.Length; j++)
                    {
                        if (j > 0) sb.Append(',');
                        var tc = msg.tool_calls[j];
                        sb.Append("{\"id\":").Append(Escape(tc.id ?? ""));
                        sb.Append(",\"type\":").Append(Escape(tc.type ?? "function"));
                        sb.Append(",\"function\":{\"name\":").Append(Escape(tc.function?.name ?? ""));
                        // OpenAI 协议: arguments 是 JSON 编码的字符串(需转义), 不是内嵌对象
                        sb.Append(",\"arguments\":").Append(Escape(string.IsNullOrEmpty(tc.function?.arguments) ? "{}" : tc.function.arguments));
                        sb.Append("}}");
                    }
                    sb.Append(']');
                }
                sb.Append('}');
            }
            sb.Append(']');
            if (options.tools != null && options.tools.Length > 0)
            {
                sb.Append(",\"tools\":[");
                for (int i = 0; i < options.tools.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    var t = options.tools[i];
                    sb.Append("{\"type\":").Append(Escape(t.type ?? "function"));
                    sb.Append(",\"function\":{\"name\":").Append(Escape(t.function?.name ?? ""));
                    sb.Append(",\"description\":").Append(Escape(t.function?.description ?? ""));
                    sb.Append(",\"parameters\":").Append(string.IsNullOrEmpty(t.function?.parameters) ? "\"{}\"" : t.function.parameters);
                    sb.Append("}}");
                }
                sb.Append(']');
                if (!string.IsNullOrEmpty(options.toolChoice))
                    sb.Append(",\"tool_choice\":").Append(Escape(options.toolChoice));
            }
            if (options.temperature.HasValue)
                sb.Append(",\"temperature\":").Append(options.temperature.Value);
            if (options.maxTokens.HasValue)
                sb.Append(",\"max_tokens\":").Append(options.maxTokens.Value);
            if (options.stream)
                sb.Append(",\"stream\":true");
            sb.Append('}');
            return sb.ToString();
        }

        public static string Escape(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 16);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
