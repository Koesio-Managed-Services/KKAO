using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Web.Script.Serialization;

namespace KKAO
{
    // Petit wrapper autour du serialiseur JSON integre au .NET Framework
    internal static class Json
    {
        static JavaScriptSerializer NewSerializer() => new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 64 };

        public static string Serialize(object value) => NewSerializer().Serialize(value);

        public static Dictionary<string, object> ParseObject(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return NewSerializer().DeserializeObject(json) as Dictionary<string, object>; }
            catch { return null; }
        }

        public static object Get(object node, params string[] path)
        {
            foreach (var key in path)
            {
                if (node is Dictionary<string, object> dict && dict.TryGetValue(key, out var next)) node = next;
                else return null;
            }
            return node;
        }

        public static string GetString(object node, params string[] path)
        {
            var value = Get(node, path);
            return value == null || value is Dictionary<string, object> || value is object[] || value is ArrayList ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
        }

        public static bool? GetBool(object node, params string[] path) => Get(node, path) is bool b ? b : (bool?)null;

        public static int? GetInt(object node, params string[] path)
        {
            switch (Get(node, path))
            {
                case int i: return i;
                case long l: return l > int.MaxValue ? int.MaxValue : l < int.MinValue ? int.MinValue : (int)l;
                case decimal d: return d > int.MaxValue ? int.MaxValue : d < int.MinValue ? int.MinValue : (int)d;
                case double f: return double.IsNaN(f) ? (int?)null : f > int.MaxValue ? int.MaxValue : f < int.MinValue ? int.MinValue : (int)f;
                case string s when int.TryParse(s, out var parsed): return parsed;
                default: return null;
            }
        }

        public static IList<object> GetArray(object node, params string[] path)
        {
            var value = Get(node, path);
            if (value is object[] array) return array;
            if (value is ArrayList list) return list.ToArray();
            return new object[0];
        }

        // Mise en forme lisible (rapport du test automatique, settings.json) ; remet aussi en clair
        // les caracteres que JavaScriptSerializer echappe par prudence (& < > ')
        public static string Pretty(string json)
        {
            var sb = new StringBuilder(json.Length * 2);
            var indent = 0;
            var inString = false;
            for (var i = 0; i < json.Length; i++)
            {
                var c = json[i];
                if (inString)
                {
                    if (c == '\\' && i + 1 < json.Length)
                    {
                        if (i + 5 < json.Length && json[i + 1] == 'u')
                        {
                            var hex = json.Substring(i + 2, 4);
                            var plain = hex == "0026" ? "&" : hex == "003c" ? "<" : hex == "003e" ? ">" : hex == "0027" ? "'" : null;
                            if (plain != null)
                            {
                                sb.Append(plain);
                                i += 5;
                                continue;
                            }
                        }
                        sb.Append(c).Append(json[i + 1]);
                        i++;
                        continue;
                    }
                    sb.Append(c);
                    if (c == '"') inString = false;
                    continue;
                }
                switch (c)
                {
                    case '"':
                        inString = true;
                        sb.Append(c);
                        break;
                    case '{':
                    case '[':
                        var close = c == '{' ? '}' : ']';
                        if (i + 1 < json.Length && json[i + 1] == close)
                        {
                            sb.Append(c).Append(close);
                            i++;
                            break;
                        }
                        sb.Append(c);
                        indent++;
                        NewLine(sb, indent);
                        break;
                    case '}':
                    case ']':
                        indent--;
                        NewLine(sb, indent);
                        sb.Append(c);
                        break;
                    case ',':
                        sb.Append(c);
                        NewLine(sb, indent);
                        break;
                    case ':':
                        sb.Append(": ");
                        break;
                    default:
                        if (!char.IsWhiteSpace(c)) sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        static void NewLine(StringBuilder sb, int indent) => sb.Append('\n').Append(' ', Math.Max(0, indent) * 2);
    }
}
