using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Milligram.Domain.Policies;

namespace Milligram.Application;

/// <summary>
/// milligram.json as a file people read and edit: init writes a short commented starter, and later edits
/// rewrite only the top-level keys whose values changed, so comments and layout survive the viewer's edits.
/// </summary>
public static class PolicyText
{
    private const int LineWidth = 100;

    public static string Starter(Initialization initialization)
    {
        var policy = initialization.Policy;
        string Value<T>(T value) => Format(Node(value), "  ");
        var levels = policy.Levels.Count == 0
            ? "[]"
            : "[\n" + string.Join(",\n", policy.Levels.Select(group => "    " + Flat(Node(group)))) + "\n  ]";
        return $$"""
            {
              // Written by `milligram init`. Milligram's README explains every key, under "Configure".
              "title": {{Value(policy.Title)}},
              // The source root and the globs to leave out, relative to this file.
              "src": {{Value(policy.Src)}},
              "exclude": {{Value(policy.Exclude)}},
              // Stripped from every namespace before drawing.
              "prefix": {{Value(policy.Prefix)}},
              // Box order, outer first.
              "order": {{Value(policy.Order)}},
              // Inferred from the dependencies: edit to match the architecture you intend. Inner (level 0) first;
              // a group shares a level. A dependency from an inner level to an outer one is drawn red.
              "levels": {{levels}},
              // Namespaces of libraries to draw as ovals, such as "Microsoft.EntityFrameworkCore".
              "foreign": {{Value(policy.Foreign)}}
            }

            """;
    }

    /// <summary>
    /// Rewrites, in place, each top-level key of <paramref name="text"/> whose value differs between
    /// <paramref name="before"/> and <paramref name="after"/>, and appends keys the text lacks. Every other
    /// byte, comments included, is kept. Throws <see cref="JsonException"/> when the text is not a JSON object.
    /// </summary>
    public static string Edit(string text, Policy before, Policy after)
    {
        var old = Node(before)!.AsObject();
        foreach (var (key, value) in Node(after)!.AsObject())
            if (!JsonNode.DeepEquals(old[key], value)) text = Set(text, key, value);
        return text;
    }

    private static JsonNode? Node<T>(T value) => JsonSerializer.SerializeToNode(value, MilligramJson.Options);

    private static string Set(string text, string key, JsonNode? value)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        reader.Read();

        var lastEnd = -1L;
        var indent = "  ";
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var name = reader.GetString();
            indent = IndentOf(bytes, (int)reader.TokenStartIndex);
            reader.Read();
            var start = reader.TokenStartIndex;
            reader.Skip();
            lastEnd = reader.BytesConsumed;
            // Policies are read case-insensitively (web defaults), so "Omit" in the file is the omit key.
            if (string.Equals(name, key, StringComparison.OrdinalIgnoreCase)) return Splice(bytes, (start, lastEnd, Format(value, indent)));
        }
        if (reader.TokenType != JsonTokenType.EndObject) throw new JsonException("milligram.json is not a JSON object.");

        var close = reader.TokenStartIndex;
        var property = $"{indent}{Quote(key)}: {Format(value, indent)}\n";
        if (close == 0 || bytes[close - 1] != '\n') property = "\n" + property;
        if (lastEnd < 0 || TrailingComma(bytes, (int)lastEnd, (int)close)) return Splice(bytes, (close, close, property));
        return Splice(bytes, (lastEnd, lastEnd, ","), (close, close, property));
    }

    /// <summary>Applies non-overlapping replacements given in text order.</summary>
    private static string Splice(byte[] bytes, params (long Start, long End, string Text)[] edits)
    {
        var result = new StringBuilder();
        var at = 0L;
        foreach (var (start, end, replacement) in edits)
        {
            result.Append(Encoding.UTF8.GetString(bytes, (int)at, (int)(start - at))).Append(replacement);
            at = end;
        }
        return result.Append(Encoding.UTF8.GetString(bytes, (int)at, bytes.Length - (int)at)).ToString();
    }

    /// <summary>The whitespace a property's line starts with, or two spaces when it shares a line with something else.</summary>
    private static string IndentOf(byte[] bytes, int position)
    {
        var lineStart = Array.LastIndexOf(bytes, (byte)'\n', position) + 1;
        var before = Encoding.UTF8.GetString(bytes, lineStart, position - lineStart);
        return string.IsNullOrWhiteSpace(before) ? before : "  ";
    }

    /// <summary>
    /// True when a comma sits between the last value and the closing brace. The parser has already checked
    /// that gap, so all it can hold is whitespace, comments, and at most one comma.
    /// </summary>
    private static bool TrailingComma(byte[] bytes, int from, int close)
    {
        for (var i = from; i < close; i++)
        {
            if (bytes[i] == ',') return true;
            if (bytes[i] != '/') continue;
            var comment = bytes.AsSpan(i, close - i);
            i += comment.StartsWith("//"u8) ? comment.IndexOf((byte)'\n') : comment.IndexOf("*/"u8) + 1;
        }
        return false;
    }

    /// <summary>On one line when it fits, otherwise one item per line.</summary>
    private static string Format(JsonNode? node, string indent)
    {
        var flat = Flat(node);
        if (indent.Length + flat.Length <= LineWidth || node is not (JsonArray or JsonObject)) return flat;
        var inner = indent + "  ";
        return node is JsonArray array
            ? "[\n" + string.Join(",\n", array.Select(item => inner + Format(item, inner))) + "\n" + indent + "]"
            : "{\n" + string.Join(",\n", node.AsObject().Select(p => $"{inner}{Quote(p.Key)}: {Format(p.Value, inner)}")) + "\n" + indent + "}";
    }

    private static string Flat(JsonNode? node) => node switch
    {
        JsonArray array => "[" + string.Join(", ", array.Select(Flat)) + "]",
        JsonObject obj => "{ " + string.Join(", ", obj.Select(p => $"{Quote(p.Key)}: {Flat(p.Value)}")) + " }",
        null => "null",
        _ => node.ToJsonString(MilligramJson.Compact),
    };

    private static string Quote(string key) => JsonSerializer.Serialize(key, MilligramJson.Compact);
}
