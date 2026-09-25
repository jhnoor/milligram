using Milligram.Domain.Model;

namespace Milligram.Domain.Policies;

/// <summary>Dotted names relative to the policy prefix: "Domain.Model", "Domain.Model.TypeNode".</summary>
public static class NamePath
{
    public static string Relative(string ns, string prefix)
    {
        if (prefix.Length == 0 || ns.Length == 0) return ns;
        if (ns == prefix) return "";
        return ns.StartsWith(prefix + ".", StringComparison.Ordinal) ? ns[(prefix.Length + 1)..] : ns;
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="target"/> or one of its dotted ancestors. "" covers everything.</summary>
    public static bool Covers(string path, string target) =>
        path.Length == 0 ||
        target == path ||
        (target.Length > path.Length && target[path.Length] == '.' && target.StartsWith(path, StringComparison.Ordinal));

    public static string Join(string left, string right) =>
        left.Length == 0 ? right : right.Length == 0 ? left : left + "." + right;

    public static string[] Segments(string path) =>
        path.Length == 0 ? [] : path.Split('.');

    public static string Last(string path)
    {
        var dot = path.LastIndexOf('.');
        return dot < 0 ? path : path[(dot + 1)..];
    }

    /// <summary>"Map&lt;K, V&gt;" becomes "Map".</summary>
    public static string BareName(string typeName)
    {
        var angle = typeName.IndexOf('<');
        return angle < 0 ? typeName : typeName[..angle];
    }

    public static string RelativeName(TypeNode type, string prefix) =>
        Join(Relative(type.Namespace, prefix), BareName(type.Name));

    /// <summary>The longest path in <paramref name="paths"/> covering <paramref name="target"/>, or null.</summary>
    public static string? LongestCovering(IEnumerable<string> paths, string target) =>
        paths.Where(p => Covers(p, target)).MaxBy(p => p.Length);
}
