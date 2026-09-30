using Milligram.Domain.Model;
using Milligram.Domain.Policies;

namespace Milligram.Domain.Hierarchy;

/// <summary>Suggests a small set of library namespaces, ranked by distinct project types that use them.</summary>
public static class ForeignLibraries
{
    private static readonly string[] Ubiquitous =
        ["System.Collections", "System.Linq", "System.Threading", "System.Runtime.CompilerServices", "System.Diagnostics.CodeAnalysis"];

    public static IReadOnlyList<string> Suggest(CodeModel model)
    {
        var libraries = model.Foreign.ToDictionary(n => n.Id, n => Root(n.Label), StringComparer.Ordinal);
        return model.Edges
            .Where(e => libraries.TryGetValue(e.To, out var root) && root is not null)
            .GroupBy(e => libraries[e.To]!, StringComparer.Ordinal)
            .OrderByDescending(g => g.Select(e => e.From).Distinct(StringComparer.Ordinal).Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Take(8)
            .Select(g => g.Key)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Two namespace segments identify most libraries; System needs three to keep choices such as Text.Json separate.</summary>
    private static string? Root(string ns)
    {
        if (ns.Length == 0 || ns == "System" || Ubiquitous.Any(prefix => NamePath.Covers(prefix, ns))) return null;
        var segments = NamePath.Segments(ns);
        return string.Join('.', segments.Take(segments[0] == "System" ? 3 : 2));
    }
}
