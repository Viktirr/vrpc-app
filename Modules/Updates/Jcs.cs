using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VRPC.Updates
{
    public sealed class JcsException : Exception
    {
        public JcsException(string message) : base(message) { }
    }

    // RFC 8785 restricted profile shared with packaging/sign-manifest.mjs:
    // objects, arrays, strings, safe integers, booleans and null only.
    public static class Jcs
    {
        private const long MaxSafeInteger = 9007199254740991;
        private static readonly Regex IntegerPattern = new("^-?(0|[1-9][0-9]*)$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

        public static string CanonicalizeToString(JsonElement element)
        {
            StringBuilder builder = new StringBuilder();
            Write(element, builder);
            return builder.ToString();
        }

        public static byte[] Canonicalize(JsonElement element)
        {
            return Encoding.UTF8.GetBytes(CanonicalizeToString(element));
        }

        private static void Write(JsonElement element, StringBuilder builder)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    WriteObject(element, builder);
                    return;
                case JsonValueKind.Array:
                    WriteArray(element, builder);
                    return;
                case JsonValueKind.String:
                    WriteString(builder, element.GetString() ?? "");
                    return;
                case JsonValueKind.Number:
                    WriteNumber(element, builder);
                    return;
                case JsonValueKind.True:
                    builder.Append("true");
                    return;
                case JsonValueKind.False:
                    builder.Append("false");
                    return;
                case JsonValueKind.Null:
                    builder.Append("null");
                    return;
                default:
                    throw new JcsException("Unsupported JSON value kind.");
            }
        }

        private static void WriteObject(JsonElement element, StringBuilder builder)
        {
            List<(string Key, JsonElement Value)> properties = new List<(string, JsonElement)>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!seen.Add(property.Name)) { throw new JcsException("Duplicate object keys are not allowed."); }
                properties.Add((property.Name, property.Value));
            }

            properties.Sort((left, right) => string.CompareOrdinal(left.Key, right.Key));

            builder.Append('{');
            for (int index = 0; index < properties.Count; index++)
            {
                if (index > 0) { builder.Append(','); }
                WriteString(builder, properties[index].Key);
                builder.Append(':');
                Write(properties[index].Value, builder);
            }
            builder.Append('}');
        }

        private static void WriteArray(JsonElement element, StringBuilder builder)
        {
            builder.Append('[');
            int index = 0;
            foreach (JsonElement item in element.EnumerateArray())
            {
                if (index++ > 0) { builder.Append(','); }
                Write(item, builder);
            }
            builder.Append(']');
        }

        private static void WriteNumber(JsonElement element, StringBuilder builder)
        {
            string raw = element.GetRawText();
            if (!IntegerPattern.IsMatch(raw))
            {
                throw new JcsException("Only integer numbers are supported.");
            }
            if (!element.TryGetInt64(out long value))
            {
                throw new JcsException("Integer number is out of range.");
            }
            if (value > MaxSafeInteger || value < -MaxSafeInteger)
            {
                throw new JcsException("Integer number is outside the JCS safe range.");
            }
            builder.Append(value == 0 ? "0" : value.ToString(CultureInfo.InvariantCulture));
        }

        private static void WriteString(StringBuilder builder, string value)
        {
            builder.Append('"');
            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                switch (character)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\b': builder.Append("\\b"); break;
                    case '\f': builder.Append("\\f"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (character < 0x20)
                        {
                            builder.Append("\\u");
                            builder.Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else if (char.IsHighSurrogate(character))
                        {
                            if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                            {
                                throw new JcsException("Lone surrogate in string.");
                            }
                            builder.Append(character).Append(value[index + 1]);
                            index++;
                        }
                        else if (char.IsLowSurrogate(character))
                        {
                            throw new JcsException("Lone surrogate in string.");
                        }
                        else
                        {
                            builder.Append(character);
                        }
                        break;
                }
            }
            builder.Append('"');
        }
    }
}
