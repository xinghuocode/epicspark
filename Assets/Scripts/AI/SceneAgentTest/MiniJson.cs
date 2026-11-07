using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace EpicSpark.AI.SceneAgentTest
{
    /// <summary>
    /// 极简 JSON 解析器(仅解析, 不序列化)。
    /// 解析结果为对象树:
    ///   object → Dictionary&lt;string, object&gt;
    ///   array  → List&lt;object&gt;
    ///   number → double
    ///   string → string
    ///   true/false → bool
    ///   null   → null
    /// 供场景Agent测试的工具调用参数解析使用。Unity 的 JsonUtility 无法表达
    /// 嵌套数组(如山脉节点 [x, y, height, width]), 因此这里自带一个轻量实现。
    /// </summary>
    public static class MiniJson
    {
        public static object Parse(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            var p = new Parser(json);
            return p.ParseValue();
        }

        // ---------- 访问辅助 ----------

        public static Dictionary<string, object> AsDict(object o) => o as Dictionary<string, object>;

        public static List<object> AsList(object o) => o as List<object>;

        public static string AsString(object o) => o as string;

        public static double? AsDouble(object o)
        {
            if (o is double d) return d;
            return null;
        }

        public static bool TryGet(Dictionary<string, object> dict, string key, out object value)
        {
            value = null;
            return dict != null && dict.TryGetValue(key, out value) && value != null;
        }

        public static string GetString(Dictionary<string, object> dict, string key, string fallback = null)
        {
            return TryGet(dict, key, out var v) ? (v as string ?? fallback) : fallback;
        }

        public static double? GetDouble(Dictionary<string, object> dict, string key)
        {
            return TryGet(dict, key, out var v) ? AsDouble(v) : (double?)null;
        }

        public static List<object> GetList(Dictionary<string, object> dict, string key)
        {
            return TryGet(dict, key, out var v) ? AsList(v) : null;
        }

        public static Dictionary<string, object> GetDict(Dictionary<string, object> dict, string key)
        {
            return TryGet(dict, key, out var v) ? AsDict(v) : null;
        }

        /// <summary>把节点数组(如 [x, y, height, width])读取为 double 数组。元素不是数字时返回 null。</summary>
        public static double[] AsDoubleArray(object o)
        {
            var list = AsList(o);
            if (list == null) return null;
            var arr = new double[list.Count];
            for (int i = 0; i < list.Count; i++)
            {
                double? d = AsDouble(list[i]);
                if (d == null) return null;
                arr[i] = d.Value;
            }
            return arr;
        }

        /// <summary>将工具执行结果用的简单对象序列化为 JSON 文本(仅支持 string/数字/bool 的嵌套 dict/list)。</summary>
        public static string Serialize(object o)
        {
            var sb = new StringBuilder();
            Write(sb, o);
            return sb.ToString();
        }

        static void Write(StringBuilder sb, object o)
        {
            switch (o)
            {
                case null: sb.Append("null"); break;
                case string s: WriteString(sb, s); break;
                case bool b: sb.Append(b ? "true" : "false"); break;
                case double d: sb.Append(d.ToString("R", CultureInfo.InvariantCulture)); break;
                case float f: sb.Append(f.ToString("R", CultureInfo.InvariantCulture)); break;
                case int i: sb.Append(i); break;
                case Dictionary<string, object> dict:
                    sb.Append('{');
                    bool first = true;
                    foreach (var kv in dict)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        WriteString(sb, kv.Key);
                        sb.Append(':');
                        Write(sb, kv.Value);
                    }
                    sb.Append('}');
                    break;
                case System.Collections.IEnumerable seq:
                    sb.Append('[');
                    first = true;
                    foreach (var item in seq)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        Write(sb, item);
                    }
                    sb.Append(']');
                    break;
                default: WriteString(sb, o.ToString()); break;
            }
        }

        static void WriteString(StringBuilder sb, string s)
        {
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
        }

        // ---------- 解析器 ----------

        class Parser
        {
            readonly string s;
            int i;

            public Parser(string json) { s = json; }

            public object ParseValue()
            {
                SkipWs();
                if (i >= s.Length) throw Error("意外的输入结尾");
                char c = s[i];
                switch (c)
                {
                    case '{': return ParseObject();
                    case '[': return ParseArray();
                    case '"': return ParseString();
                    case 't': Expect("true"); return true;
                    case 'f': Expect("false"); return false;
                    case 'n': Expect("null"); return null;
                    default:
                        if (c == '-' || (c >= '0' && c <= '9')) return ParseNumber();
                        throw Error("无法解析的值, 首字符 '" + c + "'");
                }
            }

            Dictionary<string, object> ParseObject()
            {
                var dict = new Dictionary<string, object>();
                i++; // '{'
                SkipWs();
                if (Peek('}')) { i++; return dict; }
                while (true)
                {
                    SkipWs();
                    if (i >= s.Length || s[i] != '"') throw Error("期望对象键(字符串)");
                    string key = ParseString();
                    SkipWs();
                    if (i >= s.Length || s[i] != ':') throw Error("期望 ':'");
                    i++;
                    dict[key] = ParseValue();
                    SkipWs();
                    if (Peek(',')) { i++; continue; }
                    if (Peek('}')) { i++; return dict; }
                    throw Error("期望 ',' 或 '}'");
                }
            }

            List<object> ParseArray()
            {
                var list = new List<object>();
                i++; // '['
                SkipWs();
                if (Peek(']')) { i++; return list; }
                while (true)
                {
                    list.Add(ParseValue());
                    SkipWs();
                    if (Peek(',')) { i++; continue; }
                    if (Peek(']')) { i++; return list; }
                    throw Error("期望 ',' 或 ']'");
                }
            }

            string ParseString()
            {
                var sb = new StringBuilder();
                i++; // '"'
                while (true)
                {
                    if (i >= s.Length) throw Error("字符串未闭合");
                    char c = s[i++];
                    if (c == '"') return sb.ToString();
                    if (c == '\\')
                    {
                        if (i >= s.Length) throw Error("错误的转义");
                        char e = s[i++];
                        switch (e)
                        {
                            case '"': sb.Append('"'); break;
                            case '\\': sb.Append('\\'); break;
                            case '/': sb.Append('/'); break;
                            case 'b': sb.Append('\b'); break;
                            case 'f': sb.Append('\f'); break;
                            case 'n': sb.Append('\n'); break;
                            case 'r': sb.Append('\r'); break;
                            case 't': sb.Append('\t'); break;
                            case 'u':
                                if (i + 4 > s.Length) throw Error("错误的 \\u 转义");
                                sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                                i += 4;
                                break;
                            default: throw Error("未知转义 '\\" + e + "'");
                        }
                    }
                    else sb.Append(c);
                }
            }

            double ParseNumber()
            {
                int start = i;
                if (Peek('-')) i++;
                while (i < s.Length && char.IsDigit(s[i])) i++;
                if (Peek('.')) { i++; while (i < s.Length && char.IsDigit(s[i])) i++; }
                if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
                {
                    i++;
                    if (i < s.Length && (s[i] == '+' || s[i] == '-')) i++;
                    while (i < s.Length && char.IsDigit(s[i])) i++;
                }
                return double.Parse(s.Substring(start, i - start), CultureInfo.InvariantCulture);
            }

            void Expect(string literal)
            {
                if (i + literal.Length > s.Length || s.Substring(i, literal.Length) != literal)
                    throw Error("期望 '" + literal + "'");
                i += literal.Length;
            }

            bool Peek(char c) => i < s.Length && s[i] == c;

            void SkipWs()
            {
                while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            }

            Exception Error(string msg) =>
                new FormatException($"MiniJson 解析错误 (位置 {i}): {msg}");
        }
    }
}
