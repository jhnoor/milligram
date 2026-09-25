using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Milligram.Domain.Policies;

namespace Milligram.Application;

public static class MilligramJson
{
    public static readonly JsonSerializerOptions Options = Create(indented: true);
    public static readonly JsonSerializerOptions Compact = Create(indented: false);

    private static JsonSerializerOptions Create(bool indented)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = indented,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            // Output goes to files, terminals, and fetch(): keep '+', '<', quotes in names readable.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.Converters.Add(new ProposalEntryConverter());
        return options;
    }

    /// <summary>A proposal entry is written as a bare string for a namespace, or an object for a nested group.</summary>
    private sealed class ProposalEntryConverter : JsonConverter<ProposalEntry>
    {
        public override ProposalEntry Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.String
                ? ProposalEntry.Of(reader.GetString()!)
                : ProposalEntry.Of(JsonSerializer.Deserialize<ProposalGroup>(ref reader, options)!);

        public override void Write(Utf8JsonWriter writer, ProposalEntry value, JsonSerializerOptions options)
        {
            if (value.Group is not null) JsonSerializer.Serialize(writer, value.Group, options);
            else writer.WriteStringValue(value.Namespace);
        }
    }
}

public static class JsonFile
{
    public static T? Read<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        using var stream = OpenShared(path);
        return JsonSerializer.Deserialize<T>(stream, MilligramJson.Options);
    }

    public static void Write<T>(string path, T value) =>
        WriteText(path, JsonSerializer.Serialize(value, MilligramJson.Options) + "\n");

    /// <summary>Writes to a temporary sibling and renames, so readers never see half a file.</summary>
    public static void WriteText(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, path, overwrite: true);
    }

    private static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
}
