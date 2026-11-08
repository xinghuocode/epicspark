// 语音文本工具：把 NPC 台词处理成「适合朗读」的文本。
// NPC 台词里常有括号动作描写（（他后退半步））、Markdown 符号、表情符号，
// 直接交给 TTS 会读出「左括号」之类的杂音，因此朗读前统一清洗。
using System.Text;

namespace EpicSpark.Voice
{
    /// <summary>朗读文本清洗。纯静态、无 Unity 依赖，便于单测。</summary>
    public static class VoiceText
    {
        /// <summary>去掉动作描写括号、Markdown 标记与表情符号，压平空白。结果可能为空。</summary>
        public static string SanitizeForSpeech(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";

            var sb = new StringBuilder(text.Length);
            int paren = 0;     // 圆括号嵌套深度
            int bracket = 0;   // 方括号/书名括号嵌套深度
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];

                if (c == '（' || c == '(' || c == '［') { paren++; continue; }
                if (c == '）' || c == ')') { if (paren > 0) paren--; continue; }
                if (c == '【' || c == '[' || c == '〔') { bracket++; continue; }
                if (c == '】' || c == ']' || c == '〕') { if (bracket > 0) bracket--; continue; }

                if (paren > 0 || bracket > 0) continue;

                // 无法朗读的符号：Markdown / 转义 / 表情（代理对）等
                if (c == '*' || c == '#' || c == '`' || c == '~' || c == '>' ||
                    c == '|' || c == '\\' || c == '<' || c == '^')
                    continue;
                if (char.IsSurrogate(c)) continue;
                if ((c >= '\uFE00' && c <= '\uFE0F') || (c >= '\u200B' && c <= '\u200F') ||
                    (c >= '\u2190' && c <= '\u21FF') || (c >= '\u2600' && c <= '\u27BF'))
                    continue;

                sb.Append(c);
            }

            string result = sb.ToString().Replace("\r", " ").Replace("\n", " ");
            while (result.Contains("  ")) result = result.Replace("  ", " ");
            result = result.Trim();

            // 去掉开头残留的标点，避免 TTS 一上来就是停顿
            int start = 0;
            while (start < result.Length && "，。、；：！？…—,.;:!? ".IndexOf(result[start]) >= 0) start++;
            if (start > 0) result = result.Substring(start).Trim();
            if (result.Length == 0) return "";

            // 连续标点压缩成一个，朗读更自然
            var compact = new StringBuilder(result.Length);
            char last = '\0';
            for (int i = 0; i < result.Length; i++)
            {
                char c = result[i];
                bool punct = "，。、；：！？…—,.;:!?".IndexOf(c) >= 0;
                if (punct && c == last) continue;
                compact.Append(c);
                last = c;
            }
            return compact.ToString().Trim();
        }

        /// <summary>压成单行（用于面板显示与日志）。</summary>
        public static string Flat(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string flat = text.Replace("\r", " ").Replace("\n", " ").Trim();
            while (flat.Contains("  ")) flat = flat.Replace("  ", " ");
            return flat;
        }
    }
}
