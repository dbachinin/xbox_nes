using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace NesXbox.Logic
{
    /// <summary>
    /// Small tolerant JSON reader for untrusted catalog responses whose field types vary
    /// (e.g. "developer" is a string, an array or an object). Produces Dictionary/List/string/double/bool/null.
    /// </summary>
    public static class MiniJson
    {
        public const int MaxDepth = 32;

        public static object Parse(string text)
        {
            int i = 0;
            object v = ReadValue(text, ref i, 0);
            SkipWs(text, ref i);
            if (i != text.Length) throw new FormatException("Trailing data in JSON");
            return v;
        }

        public static string Str(object node, string key) =>
            node is Dictionary<string, object> d && d.TryGetValue(key, out var v) ? v as string : null;

        public static List<object> Arr(object node, string key) =>
            node is Dictionary<string, object> d && d.TryGetValue(key, out var v) ? v as List<object> : null;

        public static double? Num(object node, string key) =>
            node is Dictionary<string, object> d && d.TryGetValue(key, out var v) && v is double x ? x : (double?)null;

        public static bool Bool(object node, string key) =>
            node is Dictionary<string, object> d && d.TryGetValue(key, out var v) && v is bool b && b;

        static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r')) i++;
        }

        static object ReadValue(string s, ref int i, int depth)
        {
            if (depth > MaxDepth) throw new FormatException("JSON nested too deeply");
            SkipWs(s, ref i);
            if (i >= s.Length) throw new FormatException("Unexpected end of JSON");
            char c = s[i];
            if (c == '{')
            {
                var d = new Dictionary<string, object>();
                i++;
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == '}') { i++; return d; }
                while (true)
                {
                    SkipWs(s, ref i);
                    if (i >= s.Length || s[i] != '"') throw new FormatException("Expected key");
                    string key = ReadString(s, ref i);
                    SkipWs(s, ref i);
                    Expect(s, ref i, ':');
                    d[key] = ReadValue(s, ref i, depth + 1);
                    SkipWs(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    Expect(s, ref i, '}');
                    return d;
                }
            }
            if (c == '[')
            {
                var l = new List<object>();
                i++;
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ']') { i++; return l; }
                while (true)
                {
                    l.Add(ReadValue(s, ref i, depth + 1));
                    SkipWs(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    Expect(s, ref i, ']');
                    return l;
                }
            }
            if (c == '"') return ReadString(s, ref i);
            if (Match(s, ref i, "true")) return true;
            if (Match(s, ref i, "false")) return false;
            if (Match(s, ref i, "null")) return null;
            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (start == i || !double.TryParse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out double num))
                throw new FormatException("Invalid JSON value at " + start);
            return num;
        }

        static bool Match(string s, ref int i, string word)
        {
            if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) return false;
            i += word.Length;
            return true;
        }

        static void Expect(string s, ref int i, char c)
        {
            if (i >= s.Length || s[i] != c) throw new FormatException("Expected '" + c + "'");
            i++;
        }

        static string ReadString(string s, ref int i)
        {
            i++; // opening quote
            var sb = new StringBuilder();
            while (true)
            {
                if (i >= s.Length) throw new FormatException("Unterminated string");
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) throw new FormatException("Bad escape");
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
                        if (i + 4 > s.Length || !int.TryParse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int cp))
                            throw new FormatException("Bad \\u escape");
                        sb.Append((char)cp);
                        i += 4;
                        break;
                    default: throw new FormatException("Bad escape");
                }
            }
        }
    }
}
