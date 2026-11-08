using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ClashDeck.Core
{
    /// <summary>
    /// 极简 JSON 解析/序列化。
    /// net48 没有 System.Text.Json，引 NuGet 会破坏“零依赖”，所以自己写一个够用的实现：
    /// 只处理对象、数组、字符串、数字、布尔、null —— 正好覆盖后端接口的全部返回。
    /// </summary>
    public static class Json
    {
        public static object Parse(string text)
        {
            if (text == null) return null;
            int i = 0;
            var v = ParseValue(text, ref i);
            return v;
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++;
        }

        private static object ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) return null;
            char c = s[i];
            if (c == '{') return ParseObject(s, ref i);
            if (c == '[') return ParseArray(s, ref i);
            if (c == '"') return ParseString(s, ref i);
            if (c == 't') { i += 4; return true; }
            if (c == 'f') { i += 5; return false; }
            if (c == 'n') { i += 4; return null; }
            return ParseNumber(s, ref i);
        }

        private static Dictionary<string, object> ParseObject(string s, ref int i)
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            i++; // {
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return d; }
            while (i < s.Length)
            {
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != '"') break;
                var key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ':') i++;
                var val = ParseValue(s, ref i);
                d[key] = val;
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == '}') { i++; break; }
                break;
            }
            return d;
        }

        private static List<object> ParseArray(string s, ref int i)
        {
            var list = new List<object>();
            i++; // [
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return list; }
            while (i < s.Length)
            {
                list.Add(ParseValue(s, ref i));
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == ']') { i++; break; }
                break;
            }
            return list;
        }

        private static string ParseString(string s, ref int i)
        {
            var sb = new StringBuilder();
            i++; // "
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '"') { i++; break; }
                if (c == '\\' && i + 1 < s.Length)
                {
                    i++;
                    char e = s[i];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (i + 4 < s.Length)
                            {
                                var hex = s.Substring(i + 1, 4);
                                if (int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                                    sb.Append((char)code);
                                i += 4;
                            }
                            break;
                        default: sb.Append(e); break;
                    }
                    i++;
                    continue;
                }
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        private static object ParseNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && ("+-.0123456789eE".IndexOf(s[i]) >= 0)) i++;
            var raw = s.Substring(start, i - start);
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
            return null;
        }

        // ------------------------------------------------------------ 序列化

        public static string Stringify(object o)
        {
            var sb = new StringBuilder();
            WriteValue(sb, o);
            return sb.ToString();
        }

        private static void WriteValue(StringBuilder sb, object o)
        {
            if (o == null) { sb.Append("null"); return; }
            if (o is string str) { WriteString(sb, str); return; }
            if (o is bool b) { sb.Append(b ? "true" : "false"); return; }
            if (o is int || o is long || o is double || o is float || o is decimal)
            {
                sb.Append(Convert.ToDouble(o).ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            if (o is IDictionary<string, object> dict)
            {
                sb.Append('{');
                bool first = true;
                foreach (var kv in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, kv.Key);
                    sb.Append(':');
                    WriteValue(sb, kv.Value);
                }
                sb.Append('}');
                return;
            }
            if (o is System.Collections.IEnumerable en)
            {
                sb.Append('[');
                bool first = true;
                foreach (var item in en)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteValue(sb, item);
                }
                sb.Append(']');
                return;
            }
            WriteString(sb, o.ToString());
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        public static Dictionary<string, object> Obj(params object[] kv)
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
            return d;
        }
    }

    /// <summary>把解析出来的 Dictionary 包一层，读写像 JS 一样顺手。</summary>
    public sealed class JObj
    {
        private readonly Dictionary<string, object> _d;
        // 原始值必须留着：数组元素是字符串/数字时 _d 为 null，
        // 只靠 _d 会让 StrList / Num 全部读成空（策略组节点列表整个消失就是这个原因）。
        private readonly object _v;

        public JObj(object raw)
        {
            _v = raw;
            _d = raw as Dictionary<string, object>;
        }

        public JObj() { _v = new Dictionary<string, object>(StringComparer.Ordinal); _d = (Dictionary<string, object>)_v; }

        public bool Exists => _v != null;
        public object Raw => _v;
        public Dictionary<string, object> Dict => _d;

        public JObj this[string key]
        {
            get
            {
                object v = null;
                _d?.TryGetValue(key, out v);
                return new JObj(v);
            }
        }

        public object Get(string key)
        {
            object v = null;
            _d?.TryGetValue(key, out v);
            return v;
        }

        public bool Has(string key) => _d != null && _d.ContainsKey(key);

        public string S(string key)
        {
            var v = Get(key);
            if (v == null) return "";
            if (v is string s) return s;
            return Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
        }

        public double D(string key)
        {
            var v = Get(key);
            if (v == null) return 0;
            if (v is double d) return d;
            if (v is bool b) return b ? 1 : 0;
            if (double.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var r)) return r;
            return 0;
        }

        public int I(string key) => (int)Math.Round(D(key));

        /// <summary>当前节点本身就是数字时（如 delays: {节点: 123}）用这个取。</summary>
        public double Num
        {
            get
            {
                var v = Raw;
                if (v is double d) return d;
                if (v is bool b) return b ? 1 : 0;
                if (v is int i) return i;
                if (v is long l) return l;
                return 0;
            }
        }

        public long L(string key) => (long)Math.Round(D(key));

        public bool B(string key)
        {
            var v = Get(key);
            if (v is bool b) return b;
            if (v is double d) return d != 0;
            var s = v as string;
            return string.Equals(s, "true", StringComparison.OrdinalIgnoreCase);
        }

        public List<JObj> List(string key)
        {
            var v = Get(key) as System.Collections.IEnumerable;
            var outList = new List<JObj>();
            if (v == null) return outList;
            foreach (var item in v) outList.Add(new JObj(item));
            return outList;
        }

        public List<string> StrList(string key) =>
            List(key).Select(x => x.Raw as string ?? "").Where(x => x.Length > 0).ToList();

        public IEnumerable<KeyValuePair<string, JObj>> Pairs
        {
            get
            {
                if (_d == null) yield break;
                foreach (var kv in _d) yield return new KeyValuePair<string, JObj>(kv.Key, new JObj(kv.Value));
            }
        }
    }
}
