using Milligram.Domain.Policies;

namespace Milligram.Application;

/// <summary>Writes a first milligram.json from what is actually in the source: no invented components.</summary>
public sealed class ProjectInitializer(ProjectPaths paths, ILanguageScanner scanner, IProjectLocator locator)
{
    public Policy Propose()
    {
        var src = Directory.Exists(Path.Combine(paths.Root, "src")) ? "src" : ".";
        var testDirectories = locator.Find(paths.Root)
            .Where(p => p.IsTest)
            .Select(p => paths.Relative(p.Directory))
            .Where(d => !d.StartsWith("..", StringComparison.Ordinal))
            .Select(d => d == "." ? "**" : d + "/**");
        var exclude = Policy.DefaultExclude.Concat(testDirectories).Distinct().ToList();
        var title = Path.GetFileName(paths.Root);

        var model = scanner.Scan(new ScanRequest(paths.Root, paths.Absolute(src), exclude, "", [], title));
        var prefix = CommonPrefix(model.Types.Select(t => t.Namespace).Where(n => n.Length > 0).Distinct().ToList());
        var order = model.Types
            .Select(t => NamePath.Segments(NamePath.Relative(t.Namespace, prefix)).FirstOrDefault())
            .OfType<string>()
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();
        return new Policy { Title = title, Src = src, Exclude = exclude, Prefix = prefix, Order = order };
    }

    /// <summary>Writes milligram.json unless it exists; always makes sure run files are git-ignored.</summary>
    public bool Initialize(bool force)
    {
        EnsureGitIgnore();
        if (File.Exists(paths.PolicyFile) && !force) return false;
        JsonFile.Write(paths.PolicyFile, Propose());
        return true;
    }

    public static string CommonPrefix(IReadOnlyList<string> namespaces)
    {
        if (namespaces.Count == 0) return "";
        var common = NamePath.Segments(namespaces[0]).ToList();
        foreach (var ns in namespaces.Skip(1))
        {
            var segments = NamePath.Segments(ns);
            var shared = 0;
            while (shared < common.Count && shared < segments.Length && common[shared] == segments[shared]) shared++;
            common.RemoveRange(shared, common.Count - shared);
        }
        return string.Join('.', common);
    }

    private void EnsureGitIgnore()
    {
        var file = Path.Combine(paths.Root, ".gitignore");
        var existing = File.Exists(file) ? File.ReadAllLines(file) : [];
        var missing = ProjectPaths.GitIgnored.Where(line => !existing.Contains(line)).ToList();
        if (missing.Count == 0) return;
        var block = (existing.Length > 0 && existing[^1].Length > 0 ? "\n" : "") + "# Milligram\n" + string.Join('\n', missing) + "\n";
        File.AppendAllText(file, block);
    }
}
