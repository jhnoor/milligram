using Milligram.Domain.Policies;

namespace Milligram.Analysis.CSharp;

/// <summary>Finds source files under a directory, honouring exclude globs matched against root-relative paths.</summary>
public static class SourceFiles
{
    private static readonly HashSet<string> SkippedDirectories = ["bin", "obj", ".git", "node_modules", ".milligram", ".vs", ".idea"];

    public static IReadOnlyList<string> Find(string directory, string root, IReadOnlyList<string> exclude)
    {
        if (!Directory.Exists(directory)) return [];
        var globs = exclude.Select(Glob.ToRegex).ToList();
        return Walk(directory)
            .Where(f => !globs.Any(g => g.IsMatch(Relative(root, f))))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    public static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    private static IEnumerable<string> Walk(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*.cs")) yield return file;
        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            if (SkippedDirectories.Contains(Path.GetFileName(child))) continue;
            foreach (var file in Walk(child)) yield return file;
        }
    }
}
