using System.Xml.Linq;
using Milligram.Application;
using Milligram.Domain.Metrics;

namespace Milligram.Analysis.Coverage;

/// <summary>Reads Cobertura XML (as written by coverlet) into line hits per project-relative file.</summary>
public sealed class CoberturaReader : ICoverageReader
{
    public LineHits Read(IEnumerable<string> reportFiles, string root)
    {
        var files = new Dictionary<string, Dictionary<int, int>>(StringComparer.Ordinal);
        foreach (var report in reportFiles)
        {
            var document = XDocument.Load(report);
            var sources = document.Descendants("source").Select(s => s.Value.Trim()).Where(s => s.Length > 0).ToList();
            foreach (var type in document.Descendants("class"))
            {
                if (type.Attribute("filename")?.Value is not { Length: > 0 } filename) continue;
                var relative = Relative(root, Resolve(filename, sources, root));
                if (IsOutside(relative)) continue;
                if (!files.TryGetValue(relative, out var lines)) files[relative] = lines = [];
                foreach (var line in type.Element("lines")?.Elements("line") ?? [])
                {
                    var number = (int?)line.Attribute("number");
                    var hits = (int?)line.Attribute("hits") ?? 0;
                    if (number is { } n) lines[n] = Math.Max(lines.GetValueOrDefault(n), hits);
                }
            }
        }
        return new LineHits(files.ToDictionary(f => f.Key, f => (IReadOnlyDictionary<int, int>)f.Value));
    }

    private static string Resolve(string filename, IReadOnlyList<string> sources, string root)
    {
        if (Path.IsPathRooted(filename)) return filename;
        return sources.Select(s => Path.Combine(s, filename)).FirstOrDefault(File.Exists) ?? Path.Combine(root, filename);
    }

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    /// <summary>On Windows, a file on another drive has no relative path, so GetRelativePath returns it whole.</summary>
    private static bool IsOutside(string relative) => relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative);
}
