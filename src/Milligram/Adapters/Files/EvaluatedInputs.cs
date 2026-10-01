using System.Security.Cryptography;
using System.Text;
using Milligram.Application;

namespace Milligram.Adapters.Files;

/// <summary>Polls evaluated imports and linked inputs even when they live outside the viewer's native watcher root.</summary>
public sealed class EvaluatedInputs : IScanInputs
{
    private static readonly HashSet<string> Skipped = ["bin", "obj", ".git", ".milligram", "node_modules", ".vs", ".idea"];
    private readonly Dictionary<string, Stamp?> files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DirectoryInput> directories = new(StringComparer.Ordinal);
    private sealed record DirectoryInput(string Root, Func<string, bool>? Include, IReadOnlyList<KeyValuePair<string, Stamp>> Files);

    public string Version { get; private set; } = "";

    public void AddAncestors(string path)
    {
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
            AddFiles(new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json", "NuGet.Config", "nuget.config", ".editorconfig" }
                .Select(name => Path.Combine(directory.FullName, name)));
    }

    /// <summary>A failed new selection still watches repairs to its previous graph while keeping the last good model.</summary>
    public void Include(EvaluatedInputs previous)
    {
        AddFiles(previous.files.Keys);
        foreach (var (key, directory) in previous.directories) AddDirectory(directory.Root, key, directory.Include);
    }

    public void AddFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths.Where(Path.IsPathFullyQualified).Distinct(StringComparer.Ordinal))
            if (!files.ContainsKey(path)) files.Add(path, Read(path));
    }

    /// <summary>Directory membership notices new wildcard inputs; exact files may still be inside normally ignored output directories.</summary>
    public void AddDirectories(IEnumerable<string> paths)
    {
        foreach (var path in paths.Where(Path.IsPathFullyQualified).Distinct(StringComparer.Ordinal))
            AddDirectory(path, path, null);
    }

    public void AddPattern(string directory, string pattern, Func<string, bool> include) => AddDirectory(directory, directory + "\0" + pattern, include);

    private void AddDirectory(string directory, string key, Func<string, bool>? include)
    {
        if (!directories.ContainsKey(key)) directories.Add(key, new DirectoryInput(directory, include, Walk(directory, include)));
    }

    public EvaluatedInputs Complete()
    {
        Version = Hash(files.Concat(directories.Values.SelectMany(directory => directory.Files).Select(pair =>
            new KeyValuePair<string, Stamp?>(pair.Key, pair.Value))));
        return this;
    }

    public string? ReadVersion()
    {
        try
        {
            return Hash(files.Keys.Select(path => new KeyValuePair<string, Stamp?>(path, Read(path)))
                .Concat(directories.Values.SelectMany(directory => Walk(directory.Root, directory.Include)).Select(pair => new KeyValuePair<string, Stamp?>(pair.Key, pair.Value))));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private static Stamp? Read(string path)
    {
        var file = new FileInfo(path);
        return file.Exists ? new Stamp(file.Length, file.LastWriteTimeUtc) : null;
    }

    private static IReadOnlyList<KeyValuePair<string, Stamp>> Walk(string directory, Func<string, bool>? include) =>
        ChangePoller.Files(directory, name => include is not null || name is not ("milligram.json" or ".gitignore"), recurse: true,
            include is null ? Skipped : null).Where(file => include is null || include(file.Key)).ToList();

    private static string Hash(IEnumerable<KeyValuePair<string, Stamp?>> entries)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        // A path may be both an explicit input and a wildcard member; preserve both observations across evaluation.
        foreach (var pair in entries.OrderBy(pair => pair.Key, StringComparer.Ordinal).ThenBy(pair => pair.Value?.Written).ThenBy(pair => pair.Value?.Length))
            hash.AppendData(Encoding.UTF8.GetBytes(pair.Key + "\0" + pair.Value?.Length + ":" + pair.Value?.Written.Ticks + "\0"));
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
