// JSON 救援：模型（尤其推理型）偶尔会吐出不合法 JSON —— 多一个逗号、少一个闭合括号、被截断。
// 这里不做完整解析器，只做"能不能救回来"的几种常见修复，让剧情不至于整幕退回确定性兜底。
using System;
using System.Collections.Generic;
using System.Text;

namespace EpicSpark.AI.OpenAI
{
    public static class JsonRepair
    {
        /// <summary>返回可被 JsonUtility 解析的 JSON；救不回来返回空串。</summary>
        public static string Repair(string json)
        {
            if (string.IsNullOrEmpty(json)) return "";
            string text = Sanitize(json);
            if (string.IsNullOrEmpty(text)) return "";

            if (LooksParseable(text)) return text;

            // 1) 去掉对象/数组结尾的多余逗号
            string noTrailing = RemoveTrailingCommas(text);
            if (noTrailing != text && LooksParseable(noTrailing)) return noTrailing;

            // 2) 从后往前找回"最后一个完整元素"的位置，补上缺失的闭合括号
            List<int> boundaries = SafeBoundaries(noTrailing);
            for (int i = boundaries.Count - 1; i >= 0; i--)
            {
                int cut = boundaries[i];
                if (cut <= 0) continue;
                string prefix = noTrailing.Substring(0, cut + 1).TrimEnd(',', ' ', '\n', '\r', '\t');
                string closed = CloseStructures(prefix);
                if (closed == null) continue;
                if (LooksParseable(closed)) return closed;
            }

            return "";
        }

        // ─────────────────────────── 基础清理 ───────────────────────────

        private static string Sanitize(string json)
        {
            string text = json.Trim();

            // ```json ... ``` 围栏
            int fence = text.IndexOf("```", StringComparison.Ordinal);
            while (fence >= 0)
            {
                int end = text.IndexOf("```", fence + 3, StringComparison.Ordinal);
                if (end < 0)
                {
                    text = text.Substring(fence + 3);
                    break;
                }
                string inner = text.Substring(fence + 3, end - fence - 3);
                int newline = inner.IndexOf('\n');
                if (newline >= 0 && newline <= 8) inner = inner.Substring(newline + 1);
                text = inner;
                fence = text.IndexOf("```", StringComparison.Ordinal);
            }

            // 只保留第一个 { 到最后一个 } 之间（去掉前后解释文字）
            int start = text.IndexOf('{');
            if (start < 0) return "";
            int lastBrace = text.LastIndexOf('}');
            if (lastBrace > start) text = text.Substring(start, lastBrace - start + 1);
            else text = text.Substring(start);

            // 去掉非法控制字符（保留 \n \r \t）
            var sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c < 0x20 && c != '\n' && c != '\r' && c != '\t') continue;
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static string RemoveTrailingCommas(string text)
        {
            var sb = new StringBuilder(text.Length);
            bool inString = false;
            bool escape = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (inString)
                {
                    sb.Append(c);
                    if (escape) escape = false;
                    else if (c == '\\') escape = true;
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '"')
                {
                    inString = true;
                    sb.Append(c);
                    continue;
                }
                if (c == ',')
                {
                    // 看下一个非空白字符是不是闭合符
                    int j = i + 1;
                    while (j < text.Length && (text[j] == ' ' || text[j] == '\n' || text[j] == '\r' || text[j] == '\t')) j++;
                    if (j < text.Length && (text[j] == '}' || text[j] == ']'))
                        continue; // 丢掉这个多余逗号
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        // ─────────────────────────── 结构修复 ───────────────────────────

        private static List<int> SafeBoundaries(string text)
        {
            var result = new List<int>();
            bool inString = false;
            bool escape = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (inString)
                {
                    if (escape) escape = false;
                    else if (c == '\\') escape = true;
                    else if (c == '"') { inString = false; result.Add(i); }
                    continue;
                }
                if (c == '"') { inString = true; continue; }
                if (c == '}' || c == ']' || c == ',') result.Add(i);
            }
            return result;
        }

        /// <summary>按 prefix 里未闭合的 { [ 补上对应的 } ]；括号顺序错乱时返回 null。</summary>
        private static string CloseStructures(string prefix)
        {
            var stack = new Stack<char>();
            bool inString = false;
            bool escape = false;
            for (int i = 0; i < prefix.Length; i++)
            {
                char c = prefix[i];
                if (inString)
                {
                    if (escape) escape = false;
                    else if (c == '\\') escape = true;
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') { inString = true; continue; }
                if (c == '{' || c == '[') stack.Push(c);
                else if (c == '}' || c == ']')
                {
                    if (stack.Count == 0) return null;
                    char open = stack.Pop();
                    if ((c == '}' && open != '{') || (c == ']' && open != '[')) return null;
                }
            }
            if (inString) return null; // 截断在字符串中间，救不回来

            var sb = new StringBuilder(prefix);
            while (stack.Count > 0)
            {
                char open = stack.Pop();
                sb.Append(open == '{' ? '}' : ']');
            }
            return sb.ToString();
        }

        /// <summary>只做"看起来能解析"的轻量检查，避免对每个候选都抛异常。</summary>
        private static bool LooksParseable(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            if (text[0] != '{' && text[0] != '[') return false;
            var stack = new Stack<char>();
            bool inString = false;
            bool escape = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (inString)
                {
                    if (escape) escape = false;
                    else if (c == '\\') escape = true;
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') { inString = true; continue; }
                if (c == '{' || c == '[') stack.Push(c);
                else if (c == '}' || c == ']')
                {
                    if (stack.Count == 0) return false;
                    char open = stack.Pop();
                    if ((c == '}' && open != '{') || (c == ']' && open != '[')) return false;
                }
            }
            return !inString && stack.Count == 0;
        }
    }
}
