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

        public static ChatMessage System(string content) => new ChatMessage { role = "system", content = content };
        public static ChatMessage User(string content) => new ChatMessage { role = "user", content = content };
        public static ChatMessage Assistant(string content) => new ChatMessage { role = "assistant", content = content };
    }

    /// <summary>聊天请求的可选参数。未设置的字段不会出现在请求体中。</summary>
    public class ChatOptions
    {
        public float? temperature;
        public int? maxTokens;
        public bool stream;
        // TODO: 后续按需扩展: top_p, stop, response_format, tools / tool_choice, seed 等
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
                sb.Append("{\"role\":").Append(Escape(messages[i].role));
                sb.Append(",\"content\":").Append(Escape(messages[i].content));
                sb.Append('}');
            }
            sb.Append(']');
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
