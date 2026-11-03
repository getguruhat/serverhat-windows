using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ServerHat;

/// JSON in the exact layout the macOS version writes (Apple's JSONSerialization and
/// JSONEncoder with prettyPrinted, sortedKeys and withoutEscapingSlashes), so servers.json
/// and API responses are byte for byte the same on both.
///
/// Values are Dictionary&lt;string, object?&gt;, List&lt;object?&gt;, string, long, double, bool or null.
internal static class Json
{
    public static object? Parse(string text)
    {
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = false });
        return Convert(document.RootElement);
    }

    private static object? Convert(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().Aggregate(new Dictionary<string, object?>(), (d, p) => { d[p.Name] = Convert(p.Value); return d; }),
        JsonValueKind.Array => element.EnumerateArray().Select(Convert).ToList(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var whole) ? (object)whole : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    public static string Write(object? value)
    {
        var text = new StringBuilder();
        Write(text, value, 0);
        return text.ToString();
    }

    /// The same values on one line with no spaces, for MCP messages: one JSON-RPC message per line.
    public static string Compact(object? value)
    {
        var text = new StringBuilder();
        WriteCompact(text, value);
        return text.ToString();
    }

    private static void WriteCompact(StringBuilder text, object? value)
    {
        switch (value)
        {
            case IDictionary<string, object?> dict:
                text.Append('{');
                var first = true;
                foreach (var key in dict.Keys.OrderBy(k => k, StringComparer.Ordinal))
                {
                    if (!first) text.Append(',');
                    first = false;
                    WriteString(text, key);
                    text.Append(':');
                    WriteCompact(text, dict[key]);
                }
                text.Append('}');
                break;
            case string or null or bool or int or long or double:
                Write(text, value, 0);
                break;
            case IDictionary<string, string> strings:
                WriteCompact(text, strings.ToDictionary(p => p.Key, p => (object?)p.Value));
                break;
            case System.Collections.IEnumerable list:
                text.Append('[');
                var firstItem = true;
                foreach (var item in list)
                {
                    if (!firstItem) text.Append(',');
                    firstItem = false;
                    WriteCompact(text, item);
                }
                text.Append(']');
                break;
            default:
                WriteString(text, value.ToString() ?? "");
                break;
        }
    }

    private static void Write(StringBuilder text, object? value, int indent)
    {
        switch (value)
        {
            case null: text.Append("null"); break;
            case bool b: text.Append(b ? "true" : "false"); break;
            case string s: WriteString(text, s); break;
            case int i: text.Append(i.ToString(CultureInfo.InvariantCulture)); break;
            case long l: text.Append(l.ToString(CultureInfo.InvariantCulture)); break;
            case double d:
                text.Append(d == Math.Floor(d) && Math.Abs(d) < 1e15 ? ((long)d).ToString(CultureInfo.InvariantCulture) : d.ToString("R", CultureInfo.InvariantCulture));
                break;
            case IDictionary<string, object?> dict:
                Container(text, '{', '}', indent, dict.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList(), (key, inner) =>
                {
                    WriteString(text, key);
                    text.Append(" : ");
                    Write(text, dict[key], inner);
                });
                break;
            case IDictionary<string, string> strings:
                Write(text, strings.ToDictionary(p => p.Key, p => (object?)p.Value), indent);
                break;
            case System.Collections.IEnumerable list:
                Container(text, '[', ']', indent, list.Cast<object?>().ToList(), (item, inner) => Write(text, item, inner));
                break;
            default: WriteString(text, value.ToString() ?? ""); break;
        }
    }

    /// Apple's layout: one item per line, two spaces per level, and "{\n\n}" when empty.
    private static void Container<T>(StringBuilder text, char open, char close, int indent, List<T> items, Action<T, int> writeItem)
    {
        var inner = indent + 2;
        text.Append(open).Append('\n');
        if (items.Count == 0) text.Append('\n');
        for (var i = 0; i < items.Count; i++)
        {
            text.Append(' ', inner);
            writeItem(items[i], inner);
            text.Append(i < items.Count - 1 ? ",\n" : "\n");
        }
        text.Append(' ', indent).Append(close);
    }

    private static void WriteString(StringBuilder text, string value)
    {
        text.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': text.Append("\\\""); break;
                case '\\': text.Append("\\\\"); break;
                case '\n': text.Append("\\n"); break;
                case '\r': text.Append("\\r"); break;
                case '\t': text.Append("\\t"); break;
                case '\b': text.Append("\\b"); break;
                case '\f': text.Append("\\f"); break;
                default:
                    if (c < 0x20) text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else text.Append(c);
                    break;
            }
        }
        text.Append('"');
    }
}
