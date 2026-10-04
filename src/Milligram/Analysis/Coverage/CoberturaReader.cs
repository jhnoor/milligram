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
        var modules = new Dictionary<string, Dictionary<string, Dictionary<int, int>>>(StringComparer.Ordinal);
        var paths = new ProjectPaths(root);
        foreach (var report in reportFiles)
        {
            var document = XDocument.Load(report);
            var sources = document.Descendants("source").Select(s => s.Value.Trim()).Where(s => s.Length > 0).ToList();
            foreach (var type in document.Descendants("class"))
            {
                if (type.Attribute("filename")?.Value is not { Length: > 0 } filename) continue;
                var relative = paths.ReportRelative(Resolve(filename, sources, root));
                if (relative is null) continue;
                if (!files.TryGetValue(relative, out var lines)) files[relative] = lines = [];
                Dictionary<int, int>? moduleLines = null;
                if (type.Ancestors("package").FirstOrDefault()?.Attribute("name")?.Value is { Length: > 0 } module)
                {
                    if (!modules.TryGetValue(module, out var moduleFiles)) modules[module] = moduleFiles = new(StringComparer.Ordinal);
                    if (!moduleFiles.TryGetValue(relative, out moduleLines)) moduleFiles[relative] = moduleLines = [];
                }
                foreach (var line in type.Element("lines")?.Elements("line") ?? [])
                {
                    var number = (int?)line.Attribute("number");
                    var hits = (int?)line.Attribute("hits") ?? 0;
                    if (number is { } n)
                    {
                        lines[n] = Math.Max(lines.GetValueOrDefault(n), hits);
                        if (moduleLines is not null) moduleLines[n] = Math.Max(moduleLines.GetValueOrDefault(n), hits);
                    }
                }
            }
        }
        return Hits(files) with { Modules = modules.ToDictionary(module => module.Key, module => Hits(module.Value), StringComparer.Ordinal) };
    }

    private static LineHits Hits(Dictionary<string, Dictionary<int, int>> files) =>
        new(files.ToDictionary(file => file.Key, file => (IReadOnlyDictionary<int, int>)file.Value, StringComparer.Ordinal));

    private static string Resolve(string filename, IReadOnlyList<string> sources, string root)
    {
        if (Path.IsPathRooted(filename)) return filename;
        return sources.Select(s => Path.Combine(s, filename)).FirstOrDefault(File.Exists) ?? Path.Combine(root, filename);
    }

}
