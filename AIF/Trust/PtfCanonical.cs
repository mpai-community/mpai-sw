using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIF.Trust;

// PTF-JSON-CANON-V1 (MPAI-PTF V1.0, Data Conventions 2). The one byte sequence a
// signed PTF object stands for, so that a signature made on one platform verifies
// on another:
//   - object members ordered by the Unicode code points of their names, at every
//     depth, no member exempt;
//   - no whitespace outside strings;
//   - strings escaped only where JSON requires it, never normalised;
//   - numbers without leading zeros, plus signs, trailing decimal points or
//     superfluous signs and zeros in an exponent;
//   - array order kept;
//   - UTF-8, no byte-order mark.
// PTF does not say how a number is written beyond what it forbids. Here an integer
// is written as its digits and any other number as the shortest text that reads back
// as the same double - the rule of RFC 8785 - with the exponent's plus sign dropped,
// as PTF requires.
public static class PtfCanonical
{
    public static byte[] Bytes(JsonNode? node) => Encoding.UTF8.GetBytes(Text(node));

    public static string Text(JsonNode? node)
    {
        var text = new StringBuilder();
        Write(node, text);
        return text.ToString();
    }

    private static void Write(JsonNode? node, StringBuilder text)
    {
        switch (node)
        {
            case null:
                text.Append("null");
                break;
            case JsonObject obj:
                text.Append('{');
                var first = true;
                foreach (var (name, value) in obj.OrderBy(p => p.Key, CodePointOrder.Instance))
                {
                    if (!first) text.Append(',');
                    first = false;
                    WriteString(name, text);
                    text.Append(':');
                    Write(value, text);
                }
                text.Append('}');
                break;
            case JsonArray array:
                text.Append('[');
                for (var i = 0; i < array.Count; i++)
                {
                    if (i > 0) text.Append(',');
                    Write(array[i], text);
                }
                text.Append(']');
                break;
            case JsonValue value:
                WriteValue(value, text);
                break;
        }
    }

    private static void WriteValue(JsonValue value, StringBuilder text)
    {
        // A value parsed holds its JsonElement; one built in code holds a CLR value,
        // read back through its JSON text.
        var element = value.TryGetValue<JsonElement>(out var parsed)
            ? parsed
            : JsonDocument.Parse(value.ToJsonString()).RootElement.Clone();
        switch (element.ValueKind)
        {
            case JsonValueKind.String: WriteString(element.GetString()!, text); break;
            case JsonValueKind.True: text.Append("true"); break;
            case JsonValueKind.False: text.Append("false"); break;
            case JsonValueKind.Null: text.Append("null"); break;
            case JsonValueKind.Number: text.Append(Number(element)); break;
            default: throw new InvalidOperationException($"No canonical form for a {element.ValueKind}.");
        }
    }

    private static string Number(JsonElement number)
    {
        if (number.TryGetInt64(out var whole)) return whole.ToString(CultureInfo.InvariantCulture);
        var d = number.GetDouble();
        if (double.IsNaN(d) || double.IsInfinity(d)) throw new InvalidOperationException("A PTF number is finite.");
        if (d == Math.Floor(d) && Math.Abs(d) < 1e21) return d.ToString("F0", CultureInfo.InvariantCulture);
        var r = d.ToString("R", CultureInfo.InvariantCulture);          // shortest round trip
        var e = r.IndexOfAny(['E', 'e']);
        if (e < 0) return r;
        var mantissa = r[..e];
        var exponent = r[(e + 1)..].TrimStart('+');
        var negative = exponent.StartsWith('-');
        exponent = exponent.TrimStart('-').TrimStart('0');
        return mantissa + "e" + (negative ? "-" : "") + (exponent.Length == 0 ? "0" : exponent);
    }

    // Only what JSON requires: the quotation mark, the reverse solidus, and the
    // control characters.
    private static void WriteString(string s, StringBuilder text)
    {
        text.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': text.Append("\\\""); break;
                case '\\': text.Append("\\\\"); break;
                case '\b': text.Append("\\b"); break;
                case '\f': text.Append("\\f"); break;
                case '\n': text.Append("\\n"); break;
                case '\r': text.Append("\\r"); break;
                case '\t': text.Append("\\t"); break;
                default:
                    if (c < 0x20) text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else text.Append(c);
                    break;
            }
        }
        text.Append('"');
    }

    // Unicode code points, not UTF-16 units: the two differ for characters beyond
    // the Basic Multilingual Plane.
    private sealed class CodePointOrder : IComparer<string>
    {
        public static readonly CodePointOrder Instance = new();

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            var a = x.EnumerateRunes().GetEnumerator();
            var b = y.EnumerateRunes().GetEnumerator();
            while (true)
            {
                var hasA = a.MoveNext();
                var hasB = b.MoveNext();
                if (!hasA || !hasB) return hasA.CompareTo(hasB);
                var c = a.Current.Value.CompareTo(b.Current.Value);
                if (c != 0) return c;
            }
        }
    }
}
