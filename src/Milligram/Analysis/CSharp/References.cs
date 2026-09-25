using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.CodeAnalysis;

namespace Milligram.Analysis.CSharp;

/// <summary>
/// Metadata references for the ad-hoc compilation: the running .NET (and ASP.NET Core) framework,
/// plus NuGet compile assets from each restored project's obj/project.assets.json.
/// </summary>
public static class References
{
    private static readonly Lazy<IReadOnlyList<MetadataReference>> Framework = new(LoadFramework);
    private static readonly ConcurrentDictionary<string, (DateTime Stamp, IReadOnlyList<string> Paths)> Assets = new();

    public static IReadOnlyList<MetadataReference> For(IEnumerable<string> projectDirectories)
    {
        var framework = Framework.Value;
        var known = framework.Select(r => Path.GetFileName(r.Display ?? "")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var packages = projectDirectories
            .SelectMany(PackageAssemblies)
            .Where(p => known.Add(Path.GetFileName(p)))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p));
        return [.. framework, .. packages];
    }

    private static IReadOnlyList<MetadataReference> LoadFramework() =>
        ((AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(p))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();

    private static IReadOnlyList<string> PackageAssemblies(string projectDirectory)
    {
        var assets = Path.Combine(projectDirectory, "obj", "project.assets.json");
        if (!File.Exists(assets)) return [];
        var stamp = File.GetLastWriteTimeUtc(assets);
        if (Assets.TryGetValue(assets, out var cached) && cached.Stamp == stamp) return cached.Paths;
        var paths = ReadAssets(assets);
        Assets[assets] = (stamp, paths);
        return paths;
    }

    private static IReadOnlyList<string> ReadAssets(string assetsFile)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(assetsFile));
            var root = doc.RootElement;
            var folders = root.GetProperty("packageFolders").EnumerateObject().Select(f => f.Name).ToList();
            var libraries = root.GetProperty("libraries");
            var target = root.GetProperty("targets").EnumerateObject().FirstOrDefault().Value;
            if (target.ValueKind != JsonValueKind.Object) return [];

            var result = new List<string>();
            foreach (var package in target.EnumerateObject())
            {
                if (!package.Value.TryGetProperty("type", out var type) || type.GetString() != "package") continue;
                if (!package.Value.TryGetProperty("compile", out var compile)) continue;
                if (!libraries.TryGetProperty(package.Name, out var library) || !library.TryGetProperty("path", out var libraryPath)) continue;
                foreach (var asset in compile.EnumerateObject().Select(a => a.Name).Where(a => a.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
                {
                    var file = folders.Select(f => Path.Combine(f, libraryPath.GetString()!, asset)).FirstOrDefault(File.Exists);
                    if (file is not null) result.Add(file);
                }
            }
            return result;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or IOException or InvalidOperationException)
        {
            return [];
        }
    }
}
