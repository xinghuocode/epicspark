// OpenAI 兼容协议的线上数据结构与小工具。
// 说明：本项目不使用 Newtonsoft / System.Text.Json，全部 JSON 走 UnityEngine.JsonUtility 或手工拼接。
using System;
using System.Globalization;
using System.Text;

// JsonUtility 只通过反射写字段，代码里不会赋值，CS0649 在此关闭。
#pragma warning disable 0649

namespace EpicSpark.AI.OpenAI
{
    // ─────────────────────────── 响应结构（JsonUtility 反序列化用） ───────────────────────────

    [Serializable]
    internal class OpenAIChatResponse
    {
        public string id;
        public string model;
        public OpenAIChoice[] choices;
        public OpenAIErrorInfo error;
    }

    [Serializable]
    internal class OpenAIChoice
    {
        public int index;
        public OpenAIMessage message;
        public string finish_reason;
    }

    [Serializable]
    internal class OpenAIMessage
    {
        public string role;
        public string content;
        /// <summary>推理型模型（DeepSeek-R1 / V4 等）会先输出思维链，正文可能为空。</summary>
        public string reasoning_content;
        public OpenAIToolCall[] tool_calls;
    }

    [Serializable]
    internal class OpenAIToolCall
    {
        public string id;
        public string type;
        public OpenAIFunctionCall function;
    }

    [Serializable]
    internal class OpenAIFunctionCall
    {
        public string name;
        public string arguments;
    }

    [Serializable]
    internal class OpenAIErrorInfo
    {
        public string message;
        public string type;
        public string code;
    }

    [Serializable]
    internal class OpenAIEmbeddingResponse
    {
        public string model;
        public OpenAIEmbeddingData[] data;
        public OpenAIErrorInfo error;
    }

    [Serializable]
    internal class OpenAIEmbeddingData
    {
        public int index;
        public float[] embedding;
    }

    // ─────────────────────────── 文本工具 ───────────────────────────

    /// <summary>JSON 手工拼接与模型输出清洗工具。Story / Npc 模块也复用这里的实现。</summary>
    public static class OpenAIText
    {
        /// <summary>把任意文本转成合法的 JSON 字符串字面量（含两侧引号）。</summary>
        public static string Quote(string value)
        {
            if (string.IsNullOrEmpty(value)) return "\"\"";
            var sb = new StringBuilder(value.Length + 16);
            sb.Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < ' ')
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>去掉 ```` ```json ```` 围栏并把第一个括号平衡的 JSON 对象抠出来；失败返回 null。</summary>
        public static string ExtractJsonObject(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            string text = raw.Trim();

            // 处理 Markdown 代码围栏
            if (text.StartsWith("```", StringComparison.Ordinal))
            {
                int newline = text.IndexOf('\n');
                if (newline > 0) text = text.Substring(newline + 1);
                int fence = text.LastIndexOf("```", StringComparison.Ordinal);
                if (fence > 0) text = text.Substring(0, fence);
                text = text.Trim();
            }

            int first = text.IndexOf('{');
            if (first < 0) return null;

            int depth = 0;
            bool inString = false;
            bool escaped = false;
            for (int i = first; i < text.Length; i++)
            {
                char c = text[i];
                if (inString)
                {
                    if (escaped) { escaped = false; continue; }
                    if (c == '\\') { escaped = true; continue; }
                    if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') { inString = true; continue; }
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) return text.Substring(first, i - first + 1);
                }
            }
            return null;
        }

        /// <summary>去掉首尾空白并截断，便于写日志。</summary>
        public static string TrimForLog(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string s = text.Trim().Replace("\r", " ").Replace("\n", " ");
            if (max > 8 && s.Length > max) s = s.Substring(0, max) + "…";
            return s;
        }

        /// <summary>把一段文本压成单行，用于提示词里的行为日志。</summary>
        public static string OneLine(string text, int max)
        {
            return TrimForLog(text, max);
        }
    }
}

#pragma warning restore 0649
