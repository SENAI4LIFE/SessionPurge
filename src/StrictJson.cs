using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SessionPurge
{
    internal sealed class StrictJson
    {
        private readonly string text;
        private int position;
        private StrictJson(string value) { text = value; }
        internal static Dictionary<string, object> Parse(string value)
        {
            if (value.Length > 1048576) throw new SafetyException("JSON row exceeds the supported limit.");
            var parser = new StrictJson(value);
            object result = parser.Value(0);
            parser.Space();
            if (parser.position != value.Length || !(result is Dictionary<string, object>)) throw new SafetyException("Invalid JSON object.");
            return (Dictionary<string, object>)result;
        }
        private void Space() { while (position < text.Length && (text[position] == ' ' || text[position] == '\t' || text[position] == '\r' || text[position] == '\n')) position++; }
        private bool Take(char c) { Space(); if (position < text.Length && text[position] == c) { position++; return true; } return false; }
        private void Need(char c) { if (!Take(c)) throw new SafetyException("Malformed JSON."); }
        private object Value(int depth)
        {
            if (depth > 48) throw new SafetyException("JSON nesting exceeds the supported limit.");
            Space();
            if (position == text.Length) throw new SafetyException("Truncated JSON.");
            char c = text[position];
            if (c == '"') return String();
            if (Take('{'))
            {
                var result = new Dictionary<string, object>(StringComparer.Ordinal);
                if (Take('}')) return result;
                do
                {
                    string key = String();
                    Need(':');
                    if (result.ContainsKey(key)) throw new SafetyException("Duplicate JSON property.");
                    result.Add(key, Value(depth + 1));
                } while (Take(','));
                Need('}');
                return result;
            }
            if (Take('['))
            {
                var result = new List<object>();
                if (Take(']')) return result;
                do { result.Add(Value(depth + 1)); } while (Take(','));
                Need(']');
                return result;
            }
            foreach (string literal in new[] { "true", "false", "null" })
            {
                if (position + literal.Length <= text.Length && string.CompareOrdinal(text, position, literal, 0, literal.Length) == 0)
                {
                    position += literal.Length;
                    return literal == "null" ? null : (object)(literal == "true");
                }
            }
            int start = position;
            if (text[position] == '-') position++;
            if (position >= text.Length) throw new SafetyException("Invalid JSON number.");
            if (text[position] == '0') position++;
            else { if (text[position] < '1' || text[position] > '9') throw new SafetyException("Invalid JSON value."); while (position < text.Length && char.IsDigit(text[position]) && text[position] <= '9') position++; }
            if (position < text.Length && text[position] == '.') { position++; Digits(); }
            if (position < text.Length && (text[position] == 'e' || text[position] == 'E')) { position++; if (position < text.Length && (text[position] == '+' || text[position] == '-')) position++; Digits(); }
            double number;
            if (!double.TryParse(text.Substring(start, position - start), NumberStyles.Float, CultureInfo.InvariantCulture, out number) || double.IsInfinity(number)) throw new SafetyException("Invalid JSON number.");
            return number;
        }
        private void Digits()
        {
            int start = position;
            while (position < text.Length && text[position] >= '0' && text[position] <= '9') position++;
            if (start == position) throw new SafetyException("Invalid JSON number.");
        }
        private string String()
        {
            Need('"');
            var result = new StringBuilder();
            while (position < text.Length)
            {
                char c = text[position++];
                if (c == '"')
                {
                    string value = result.ToString();
                    for (int i = 0; i < value.Length; i++)
                    {
                        if (char.IsHighSurrogate(value[i])) { if (++i >= value.Length || !char.IsLowSurrogate(value[i])) throw new SafetyException("Invalid Unicode surrogate pair."); }
                        else if (char.IsLowSurrogate(value[i])) throw new SafetyException("Invalid Unicode surrogate pair.");
                    }
                    return value;
                }
                if (c < 32) throw new SafetyException("Invalid JSON string.");
                if (c == '\\')
                {
                    if (position >= text.Length) throw new SafetyException("Truncated JSON escape.");
                    char escape = text[position++];
                    switch (escape)
                    {
                        case '"': c = '"'; break;
                        case '\\': c = '\\'; break;
                        case '/': c = '/'; break;
                        case 'b': c = '\b'; break;
                        case 'f': c = '\f'; break;
                        case 'n': c = '\n'; break;
                        case 'r': c = '\r'; break;
                        case 't': c = '\t'; break;
                        case 'u':
                            ushort code;
                            if (position + 4 > text.Length || !ushort.TryParse(text.Substring(position, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code)) throw new SafetyException("Invalid Unicode escape.");
                            c = (char)code;
                            position += 4;
                            break;
                        default: throw new SafetyException("Invalid JSON escape.");
                    }
                }
                result.Append(c);
            }
            throw new SafetyException("Unterminated JSON string.");
        }
        internal static string Text(Dictionary<string, object> value, string key)
        {
            object result;
            return value.TryGetValue(key, out result) ? result as string : null;
        }
        internal static Dictionary<string, object> Object(Dictionary<string, object> value, string key)
        {
            object result;
            return value.TryGetValue(key, out result) ? result as Dictionary<string, object> : null;
        }
    }
}
