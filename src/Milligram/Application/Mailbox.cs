using System.Text.Json;
using System.Text.Json.Nodes;
using Milligram.Domain.Mail;

namespace Milligram.Application;

/// <summary>A mailbox directory: one JSON file per message, read oldest first, deleted once taken.</summary>
public sealed class Mailbox(string directory)
{
    public string Directory { get; } = directory;

    public MailMessage Post(string op, JsonObject? data = null)
    {
        var at = DateTimeOffset.UtcNow;
        var name = MailMessage.FileName(at, Guid.NewGuid().ToString("N")[..8], op);
        var message = new MailMessage(Path.GetFileNameWithoutExtension(name), op, at, data ?? []);
        JsonFile.Write(Path.Combine(Directory, name), message);
        return message;
    }

    public int Count => System.IO.Directory.Exists(Directory) ? Pending().Count() : 0;

    public IReadOnlyList<MailMessage> Take(bool keep = false)
    {
        if (!System.IO.Directory.Exists(Directory)) return [];
        var messages = new List<MailMessage>();
        foreach (var file in Pending())
        {
            if (Parse(file) is { } message) messages.Add(message);
            if (!keep) TryDelete(file);
        }
        return messages;
    }

    private IEnumerable<string> Pending() =>
        System.IO.Directory.GetFiles(Directory, "*.json").OrderBy(Path.GetFileName, StringComparer.Ordinal);

    private static MailMessage? Parse(string file)
    {
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(file)) as JsonObject;
            if (node is null) return null;
            var op = node["op"]?.GetValue<string>() ?? "unknown";
            var at = node["at"] is { } a ? a.Deserialize<DateTimeOffset>() : File.GetLastWriteTimeUtc(file);
            var data = node["data"] as JsonObject ?? StripEnvelope(node);
            return new MailMessage(Path.GetFileNameWithoutExtension(file), op, at, (JsonObject)data.DeepClone());
        }
        catch (Exception e) when (e is JsonException or IOException or InvalidOperationException or FormatException)
        {
            return new MailMessage(Path.GetFileNameWithoutExtension(file), "invalid", DateTimeOffset.UtcNow,
                new JsonObject { ["error"] = e.Message });
        }
    }

    /// <summary>Accepts hand-written mail like {"op":"display","context":"real"} without a data envelope.</summary>
    private static JsonObject StripEnvelope(JsonObject node)
    {
        var data = new JsonObject();
        foreach (var (key, value) in node)
            if (key is not ("op" or "id" or "at")) data[key] = value?.DeepClone();
        return data;
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); }
        catch (IOException) { }
    }
}
