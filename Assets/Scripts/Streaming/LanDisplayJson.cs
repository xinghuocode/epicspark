// 展示端接口 · 最小 JSON 拼装器。
// 约束：不依赖 Newtonsoft / System.Text.Json / HttpClient，只用 .NET 标准库与 C# 8 语法；
// 采集线程与 HTTP 线程都可能用它拼 JSON，因此不做任何静态可变状态。
using System.Globalization;
using System.Text;

namespace EpicSpark.Streaming
{
    /// <summary>手写 JSON 拼装器（对象/数组/字符串/数字/布尔/null）。</summary>
    internal sealed class JsonBuilder
    {
        private readonly StringBuilder _sb;
        private bool _needComma;

        public JsonBuilder(int capacity = 512)
        {
            _sb = new StringBuilder(capacity);
        }

        public JsonBuilder BeginObject()
        {
            Separator();
            _sb.Append('{');
            _needComma = false;
            return this;
        }

        public JsonBuilder EndObject()
        {
            _sb.Append('}');
            _needComma = true;
            return this;
        }

        public JsonBuilder BeginArray()
        {
            Separator();
            _sb.Append('[');
            _needComma = false;
            return this;
        }

        public JsonBuilder EndArray()
        {
            _sb.Append(']');
            _needComma = true;
            return this;
        }

        public JsonBuilder Name(string name)
        {
            Separator();
            AppendQuoted(name);
            _sb.Append(':');
            _needComma = false;
            return this;
        }

        public JsonBuilder Value(string value)
        {
            Separator();
            if (value == null) _sb.Append("null");
            else AppendQuoted(value);
            _needComma = true;
            return this;
        }

        public JsonBuilder Value(int value)
        {
            Separator();
            _sb.Append(value.ToString(CultureInfo.InvariantCulture));
            _needComma = true;
            return this;
        }

        public JsonBuilder Value(long value)
        {
            Separator();
            _sb.Append(value.ToString(CultureInfo.InvariantCulture));
            _needComma = true;
            return this;
        }

        public JsonBuilder Value(double value)
        {
            Separator();
            if (double.IsNaN(value) || double.IsInfinity(value)) _sb.Append('0');
            else _sb.Append(value.ToString("0.###", CultureInfo.InvariantCulture));
            _needComma = true;
            return this;
        }

        public JsonBuilder Value(bool value)
        {
            Separator();
            _sb.Append(value ? "true" : "false");
            _needComma = true;
            return this;
        }

        public JsonBuilder Null()
        {
            Separator();
            _sb.Append("null");
            _needComma = true;
            return this;
        }

        public JsonBuilder Property(string name, string value)
        {
            return Name(name).Value(value);
        }

        public JsonBuilder Property(string name, int value)
        {
            return Name(name).Value(value);
        }

        public JsonBuilder Property(string name, long value)
        {
            return Name(name).Value(value);
        }

        public JsonBuilder Property(string name, bool value)
        {
            return Name(name).Value(value);
        }

        public JsonBuilder Property(string name, double value)
        {
            return Name(name).Value(value);
        }

        public override string ToString()
        {
            return _sb.ToString();
        }

        private void Separator()
        {
            if (_needComma) _sb.Append(',');
        }

        private void AppendQuoted(string value)
        {
            _sb.Append('"');
            int length = value.Length;
            for (int i = 0; i < length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '"': _sb.Append("\\\""); break;
                    case '\\': _sb.Append("\\\\"); break;
                    case '\b': _sb.Append("\\b"); break;
                    case '\f': _sb.Append("\\f"); break;
                    case '\n': _sb.Append("\\n"); break;
                    case '\r': _sb.Append("\\r"); break;
                    case '\t': _sb.Append("\\t"); break;
                    default:
                        if (c < ' ')
                        {
                            _sb.Append("\\u");
                            _sb.Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            _sb.Append(c);
                        }
                        break;
                }
            }
            _sb.Append('"');
        }
    }
}
