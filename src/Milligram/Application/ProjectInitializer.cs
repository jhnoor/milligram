using Milligram.Domain.Hierarchy;
using Milligram.Domain.Model;
using Milligram.Domain.Policies;

namespace Milligram.Application;

/// <summary>A first policy and the dependencies its inferred levels leave pointing outward.</summary>
public sealed record Initialization(Policy Policy, IReadOnlyList<DependencyEdge> Outward)
{
    private const int ListedOutward = 10;

    /// <summary>What init decided, for the console: the levels, and which arrows start out red.</summary>
    public IReadOnlyList<string> Describe()
    {
        if (Policy.Levels.Count == 0) return [];
        var lines = new List<string> { "Levels, inferred from the dependencies (inner first; edit them in milligram.json):" };
        lines.AddRange(Policy.Levels.Select((names, level) => $"  L{level}  {string.Join(", ", names)}"));
        if (Outward.Count == 0)
        {
            lines.Add("No dependency cycles between top-level namespaces.");
            return lines;
        }
        lines.Add($"{Outward.Count} {(Outward.Count == 1 ? "dependency points" : "dependencies point")} outward (red), the lightest links in dependency cycles:");
        lines.AddRange(Outward
            .OrderBy(e => e.Count).ThenBy(e => e.From, StringComparer.Ordinal).ThenBy(e => e.To, StringComparer.Ordinal)
            .Take(ListedOutward)
            .Select(e => $"  {e.From} -> {e.To} ({e.Count} {(e.Count == 1 ? "reference" : "references")})"));
        if (Outward.Count > ListedOutward) lines.Add($"  and {Outward.Count - ListedOutward} more");
        return lines;
    }
}

/// <summary>Writes a first milligram.json from what is actually in the source: no invented components.</summary>
public sealed class ProjectInitializer(ProjectPaths paths, ILanguageScanner scanner, IProjectLocator locator)
{
    /// <summary>Levels come from the dependencies (see <see cref="Layering"/>); boxes are ordered outer first, as drawn.</summary>
    public Initialization Propose()
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
        var layers = Layering.TopLevel(model, prefix);
        var order = layers.Levels.Reverse().SelectMany(names => names).ToList();
        var policy = new Policy { Title = title, Src = src, Exclude = exclude, Prefix = prefix, Order = order, Levels = layers.Levels };
        return new Initialization(policy, layers.Outward);
    }

    /// <summary>Writes a commented milligram.json unless one exists (null then); always makes sure run files are git-ignored.</summary>
    public Initialization? Initialize(bool force)
    {
        EnsureGitIgnore();
        if (File.Exists(paths.PolicyFile) && !force) return null;
        var initialization = Propose();
        JsonFile.WriteText(paths.PolicyFile, PolicyText.Starter(initialization));
        return initialization;
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
