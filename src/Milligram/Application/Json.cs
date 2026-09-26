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
        try
        {
            Replace(temp, path);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>Reads a whole file, sharing it the way <see cref="Read{T}"/> does, so an editor can still save it meanwhile.</summary>
    public static string ReadText(string path)
    {
        using var reader = new StreamReader(OpenShared(path));
        return reader.ReadToEnd();
    }

    private const int ReplaceAttempts = 10;

    /// <summary>
    /// On Windows, renaming over a file fails while anyone has it open, even a reader that shares deletes: a virus
    /// scanner, the search indexer, an editor, or another Milligram. Those opens are brief, so retry for about a second.
    /// </summary>
    internal static void Replace(string temp, string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (Exception e) when (IsLocked(e) && attempt < ReplaceAttempts)
            {
                Thread.Sleep(20 * attempt);
            }
            catch (Exception e) when (IsLocked(e))
            {
                throw new MilligramException($"Could not replace {path}, because another program keeps it open. Close that program, or wait a moment, and try again. ({e.Message})");
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private const int SharingViolation = unchecked((int)0x80070020);
    private const int LockViolation = unchecked((int)0x80070021);

    /// <summary>Windows reports a file held open as a sharing violation, or as access denied.</summary>
    private static bool IsLocked(Exception e) =>
        OperatingSystem.IsWindows() && e is UnauthorizedAccessException or IOException { HResult: SharingViolation or LockViolation };

    private static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
}
