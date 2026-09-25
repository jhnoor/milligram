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
            return new BuildProject(path, Path.GetFileNameWithoutExtension(path), IsTest(document), references);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    private static bool IsTest(XDocument document) =>
        document.Descendants().Any(e =>
            (e.Name.LocalName == "PackageReference" && TestPackages.Contains(e.Attribute("Include")?.Value ?? "")) ||
            (e.Name.LocalName == "IsTestProject" && e.Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)));

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
