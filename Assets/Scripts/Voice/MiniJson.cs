using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace EpicSpark.Voice
{
    // Minimal JSON parser/serializer helper. No third-party dependency and pure
    // managed code, so it is safe to call from background threads (unlike most
    // UnityEngine APIs, including JsonUtility).
    //
    // Parses into: Dictionary<string,object> / List<object> / string / double /
    // bool / null. Access nested values through Get(...) with a key path.
    public static class MiniJson
    {
        public static object Parse(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            int i = 0;
            return ParseValue(json, ref i);
        }

        static void SkipWs(string s, ref int i)
        {
            while (i < s.Length)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r') i++;
                else break;
            }
        }

        static object ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) return null;
            char c = s[i];
            switch (c)
            {
                case '{': return ParseObject(s, ref i);
                case '[': return ParseArray(s, ref i);
                case '"': return ParseString(s, ref i);
                case 't': Expect(s, ref i, "true"); return true;
                case 'f': Expect(s, ref i, "false"); return false;
                case 'n': Expect(s, ref i, "null"); return null;
                default: return ParseNumber(s, ref i);
            }
        }

        static void Expect(string s, ref int i, string word)
        {
            if (i + word.Length > s.Length || string.CompareOrdinal(s, i, word, 0, word.Length) != 0)
                throw new FormatException("Invalid JSON token at " + i);
            i += word.Length;
        }

        static Dictionary<string, object> ParseObject(string s, ref int i)
        {
            var d = new Dictionary<string, object>();
            i++; // '{'
            while (true)
            {
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("Unterminated object");
                if (s[i] == '}') { i++; return d; }
                if (s[i] == ',') { i++; continue; }
                string key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("Expected ':' at " + i);
                i++;
                d[key] = ParseValue(s, ref i);
            }
        }

        static List<object> ParseArray(string s, ref int i)
        {
            var list = new List<object>();
            i++; // '['
            while (true)
            {
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("Unterminated array");
                if (s[i] == ']') { i++; return list; }
                if (s[i] == ',') { i++; continue; }
                list.Add(ParseValue(s, ref i));
            }
        }

        static string ParseString(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length || s[i] != '"') throw new FormatException("Expected string at " + i);
            i++;
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c == '\\')
                {
                    if (i >= s.Length) break;
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
                            if (i + 4 <= s.Length)
                            {
                                int code = int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                                sb.Append((char)code);
                                i += 4;
                            }
                            break;
                        default: sb.Append(e); break;
                    }
                }
                else sb.Append(c);
            }
            throw new FormatException("Unterminated string");
        }

        static object ParseNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length)
            {
                char c = s[i];
                if ((c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E') i++;
                else break;
            }
            string num = s.Substring(start, i - start);
            double d;
            if (double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            return null;
        }

        // ---- accessors ------------------------------------------------------

        public static Dictionary<string, object> AsDict(object o) => o as Dictionary<string, object>;

        public static object Get(object root, params string[] path)
        {
            object cur = root;
            foreach (var key in path)
            {
                var d = cur as Dictionary<string, object>;
                if (d == null || !d.TryGetValue(key, out cur)) return null;
            }
            return cur;
        }

        public static string GetString(object root, params string[] path)
        {
            var v = Get(root, path);
            if (v == null) return null;
            if (v is string s) return s;
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static bool GetBool(object root, params string[] path)
        {
            var v = Get(root, path);
            if (v is bool b) return b;
            if (v is double d) return d != 0;
            if (v is string s && bool.TryParse(s, out var r)) return r;
            return false;
        }

        // ---- writing --------------------------------------------------------

        public static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
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
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        public static string Quote(string s) => "\"" + Escape(s) + "\"";
    }
}
