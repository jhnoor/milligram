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
            return new BuildProject(path, Path.GetFileNameWithoutExtension(path), IsTest(document), references, File.Exists(AssetsFile(path)));
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

    private static bool IsTest(XDocument document) =>
        PackageReferences(document).Any(TestPackages.Contains) ||
        document.Descendants().Any(e => e.Name.LocalName == "IsTestProject" && e.Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase));

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
