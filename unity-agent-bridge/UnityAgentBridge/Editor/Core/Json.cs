using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace UnityAgentBridge
{
    /// <summary>
    /// Dependency-free JSON reader / writer.
    /// Parse returns: Dictionary&lt;string, object&gt;, List&lt;object&gt;, string, double, bool or null.
    /// </summary>
    public static class Json
    {
        // ---------------------------------------------------------------- parse

        public static object Parse(string json)
        {
            if (json == null) throw new ArgumentNullException("json");
            int index = 0;
            object value = ParseValue(json, ref index);
            SkipSpace(json, ref index);
            if (index != json.Length)
                throw new FormatException("JSON 尾部存在多余字符（位置 " + index + "）");
            return value;
        }

        static object ParseValue(string s, ref int i)
        {
            SkipSpace(s, ref i);
            if (i >= s.Length) throw new FormatException("JSON 意外结束");

            char c = s[i];
            switch (c)
            {
                case '{': return ParseObject(s, ref i);
                case '[': return ParseArray(s, ref i);
                case '"': return ParseString(s, ref i);
                case 't':
                    Expect(s, ref i, "true");
                    return true;
                case 'f':
                    Expect(s, ref i, "false");
                    return false;
                case 'n':
                    Expect(s, ref i, "null");
                    return null;
                default:
                    return ParseNumber(s, ref i);
            }
        }

        static Dictionary<string, object> ParseObject(string s, ref int i)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            i++; // {
            SkipSpace(s, ref i);
            if (i < s.Length && s[i] == '}')
            {
                i++;
                return result;
            }

            while (true)
            {
                SkipSpace(s, ref i);
                if (i >= s.Length || s[i] != '"') throw new FormatException("JSON 对象缺少键名（位置 " + i + "）");
                string key = ParseString(s, ref i);
                SkipSpace(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("JSON 对象缺少冒号（位置 " + i + "）");
                i++;
                result[key] = ParseValue(s, ref i);
                SkipSpace(s, ref i);
                if (i >= s.Length) throw new FormatException("JSON 对象意外结束");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return result; }
                throw new FormatException("JSON 对象缺少逗号或右花括号（位置 " + i + "）");
            }
        }

        static List<object> ParseArray(string s, ref int i)
        {
            var result = new List<object>();
            i++; // [
            SkipSpace(s, ref i);
            if (i < s.Length && s[i] == ']')
            {
                i++;
                return result;
            }

            while (true)
            {
                result.Add(ParseValue(s, ref i));
                SkipSpace(s, ref i);
                if (i >= s.Length) throw new FormatException("JSON 数组意外结束");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return result; }
                throw new FormatException("JSON 数组缺少逗号或右方括号（位置 " + i + "）");
            }
        }

        static string ParseString(string s, ref int i)
        {
            var sb = new StringBuilder();
            i++; // opening quote
            while (true)
            {
                if (i >= s.Length) throw new FormatException("JSON 字符串未闭合");
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }

                if (i >= s.Length) throw new FormatException("JSON 转义序列未完成");
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
                        if (i + 4 > s.Length) throw new FormatException("JSON \\u 转义未完成");
                        sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16));
                        i += 4;
                        break;
                    default:
                        throw new FormatException("JSON 非法转义 \\" + e);
                }
            }
        }

        static object ParseNumber(string s, ref int i)
        {
            int start = i;
            if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
            while (i < s.Length && char.IsDigit(s[i])) i++;
            if (i < s.Length && s[i] == '.')
            {
                i++;
                while (i < s.Length && char.IsDigit(s[i])) i++;
            }
            if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
            {
                i++;
                if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
                while (i < s.Length && char.IsDigit(s[i])) i++;
            }

            if (i == start) throw new FormatException("JSON 无法识别的值（位置 " + start + "）");

            string text = s.Substring(start, i - start);
            double d;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                throw new FormatException("JSON 数字格式错误: " + text);
            return d;
        }

        static void Expect(string s, ref int i, string literal)
        {
            if (i + literal.Length > s.Length || string.CompareOrdinal(s, i, literal, 0, literal.Length) != 0)
                throw new FormatException("JSON 期望 " + literal + "（位置 " + i + "）");
            i += literal.Length;
        }

        static void SkipSpace(string s, ref int i)
        {
            while (i < s.Length)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r') { i++; continue; }
                break;
            }
        }

        // -------------------------------------------------------------- serialize

        public static string Serialize(object value)
        {
            var sb = new StringBuilder(256);
            Write(sb, value);
            return sb.ToString();
        }

        /// <summary>Serialize with 2-space indentation (readable CLI output).</summary>
        public static string SerializePretty(object value)
        {
            var sb = new StringBuilder(256);
            WritePretty(sb, value, 0);
            return sb.ToString();
        }

        static void Write(StringBuilder sb, object value)
        {
            if (value == null) { sb.Append("null"); return; }

            var text = value as string;
            if (text != null) { WriteString(sb, text); return; }

            if (value is bool) { sb.Append(((bool)value) ? "true" : "false"); return; }
            if (value is char) { WriteString(sb, value.ToString()); return; }

            if (value is float)
            {
                float f = (float)value;
                if (float.IsNaN(f) || float.IsInfinity(f)) sb.Append("null");
                else sb.Append(f.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            if (value is double)
            {
                double d = (double)value;
                if (double.IsNaN(d) || double.IsInfinity(d)) sb.Append("null");
                else sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            if (value is decimal) { sb.Append(((decimal)value).ToString(CultureInfo.InvariantCulture)); return; }
            if (value is byte || value is sbyte || value is short || value is ushort ||
                value is int || value is uint || value is long || value is ulong)
            {
                sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
            }

            var dict = value as IDictionary;
            if (dict != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (DictionaryEntry entry in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, Convert.ToString(entry.Key, CultureInfo.InvariantCulture));
                    sb.Append(':');
                    Write(sb, entry.Value);
                }
                sb.Append('}');
                return;
            }

            var list = value as IEnumerable;
            if (list != null)
            {
                sb.Append('[');
                bool first = true;
                foreach (object item in list)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    Write(sb, item);
                }
                sb.Append(']');
                return;
            }

            WriteString(sb, Convert.ToString(value, CultureInfo.InvariantCulture));
        }

        static void WritePretty(StringBuilder sb, object value, int depth)
        {
            string pad = new string(' ', depth * 2);
            string padInner = new string(' ', (depth + 1) * 2);

            var dict = value as IDictionary;
            if (dict != null && dict.Count > 0)
            {
                sb.Append("{\n");
                bool first = true;
                foreach (DictionaryEntry entry in dict)
                {
                    if (!first) sb.Append(",\n");
                    first = false;
                    sb.Append(padInner);
                    WriteString(sb, Convert.ToString(entry.Key, CultureInfo.InvariantCulture));
                    sb.Append(": ");
                    WritePretty(sb, entry.Value, depth + 1);
                }
                sb.Append('\n').Append(pad).Append('}');
                return;
            }

            var list = value as IEnumerable;
            if (list != null && !(value is string))
            {
                var items = new List<object>();
                foreach (object item in list) items.Add(item);
                if (items.Count > 0)
                {
                    sb.Append("[\n");
                    for (int i = 0; i < items.Count; i++)
                    {
                        if (i > 0) sb.Append(",\n");
                        sb.Append(padInner);
                        WritePretty(sb, items[i], depth + 1);
                    }
                    sb.Append('\n').Append(pad).Append(']');
                    return;
                }
            }

            Write(sb, value);
        }

        static void WriteString(StringBuilder sb, string s)
        {
            if (s == null) { sb.Append("null"); return; }
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
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
                        if (c < 0x20 || c == 0x7f)
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
