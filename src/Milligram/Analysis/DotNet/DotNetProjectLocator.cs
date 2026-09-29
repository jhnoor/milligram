using System.Text.Json;
using System.Xml.Linq;
using Milligram.Application;

namespace Milligram.Analysis.DotNet;

/// <summary>Finds .csproj files, their project references, and which of them are test projects.</summary>
public sealed class DotNetProjectLocator : IProjectLocator
{
    private static readonly HashSet<string> SkippedDirectories = ["bin", "obj", ".git", "node_modules", ".milligram", ".vs", ".idea"];

    private static readonly HashSet<string> TestPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.NET.Test.Sdk", "xunit", "xunit.v3", "NUnit", "MSTest", "MSTest.TestFramework", "TUnit",
    };

    private static readonly HashSet<string> FrameworkTargets = new(StringComparer.OrdinalIgnoreCase)
    {
        "net11", "net20", "net35", "net40", "net403", "net45", "net451", "net452",
        "net46", "net461", "net462", "net47", "net471", "net472", "net48", "net481",
    };

    public IReadOnlyList<BuildProject> Find(string root) =>
        Walk(root).Select(Read).OfType<BuildProject>().OrderBy(p => p.Path, StringComparer.Ordinal).ToList();

    private static BuildProject? Read(string path)
    {
        try
        {
            var document = XDocument.Load(path);
            var directory = Path.GetDirectoryName(path)!;
            var references = document.Descendants()
                .Where(e => e.Name.LocalName == "ProjectReference")
                .Select(e => e.Attribute("Include")?.Value)
                .OfType<string>()
                .Select(r => Path.GetFullPath(Path.Combine(directory, r.Replace('\\', Path.DirectorySeparatorChar))))
                .ToList();
            return new BuildProject(path, Path.GetFileNameWithoutExtension(path), IsTest(document, directory), references, File.Exists(AssetsFile(path)))
            {
                IsSdkStyle = document.Root?.Attribute("Sdk") is not null || document.Descendants().Any(e =>
                    e.Name.LocalName == "Sdk" || e.Name.LocalName == "Import" && e.Attribute("Sdk") is not null),
                TargetsNetFramework = document.Descendants().Any(e => e.Name.LocalName == "TargetFrameworkVersion" ||
                    e.Name.LocalName is "TargetFramework" or "TargetFrameworks" && e.Value.Split(';').Any(t => FrameworkTargets.Contains(t.Trim()))),
            };
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    public bool? UsesPackage(BuildProject project, string package)
    {
        try
        {
            if (PackageReferences(XDocument.Load(project.Path)).Contains(package, StringComparer.OrdinalIgnoreCase)) return true;
            var assets = AssetsFile(project.Path);
            if (!File.Exists(assets)) return null;
            using var stream = File.OpenRead(assets);
            using var document = JsonDocument.Parse(stream);
            return document.RootElement.GetProperty("libraries").EnumerateObject()
                .Any(library => library.Name.StartsWith(package + "/", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception e) when (e is System.Xml.XmlException or JsonException or KeyNotFoundException or InvalidOperationException
                                       or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Written by `dotnet restore`: every package the project resolved, direct or not.</summary>
    private static string AssetsFile(string projectPath) => Path.Combine(Path.GetDirectoryName(projectPath)!, "obj", "project.assets.json");

    private static IEnumerable<string> PackageReferences(XDocument document) =>
        document.Descendants().Where(e => e.Name.LocalName == "PackageReference").Select(e => e.Attribute("Include")?.Value).OfType<string>();

    private static bool IsTest(XDocument document, string directory) =>
        PackageReferences(document).Any(TestPackages.Contains) ||
        LegacyPackages(directory).Any(TestPackages.Contains) ||
        document.Descendants().Any(e => e.Name.LocalName == "IsTestProject" && e.Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase));

    /// <summary>Legacy test projects usually name their test framework in packages.config.</summary>
    private static IEnumerable<string> LegacyPackages(string directory)
    {
        var path = Path.Combine(directory, "packages.config");
        if (!File.Exists(path)) return [];
        try
        {
            return XDocument.Load(path).Descendants().Where(e => e.Name.LocalName == "package")
                .Select(e => e.Attribute("id")?.Value).OfType<string>().ToList();
        }
        catch (System.Xml.XmlException) { return []; }
    }

    private static IEnumerable<string> Walk(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*.csproj")) yield return file;
        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            if (SkippedDirectories.Contains(Path.GetFileName(child))) continue;
            foreach (var file in Walk(child)) yield return file;
        }
    }
}
