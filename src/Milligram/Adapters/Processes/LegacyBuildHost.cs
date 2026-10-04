using System.Xml;
using System.Xml.Linq;

namespace Milligram.Adapters.Processes;

/// <summary>Rejects unsupported legacy host paths before Roslyn starts a process that cannot connect.</summary>
internal static class LegacyBuildHost
{
    public static void Check(string directory, bool windows, IEnumerable<string> projects)
    {
        if (PathError(directory, windows) is not { } error) return;
        foreach (var project in projects.Order(StringComparer.Ordinal))
            if (UsesFrameworkHost(project)) throw new InvalidOperationException($"{error} Project requiring the legacy host: {project}");
    }

    public static string? PathError(string directory, bool windows)
    {
        var config = Path.Combine(directory, "BuildHost-net472", "Microsoft.CodeAnalysis.Workspaces.MSBuild.BuildHost.exe.config");
        return windows && config.Length >= 260
            ? $"Legacy MSBuild host configuration path exceeds the Windows .NET Framework limit ({config.Length} characters): {config}. Install Milligram under a shorter --tool-path before evaluating old-style projects."
            : null;
    }

    /// <summary>Matches Roslyn 5.9's host selection; the evaluated target framework alone does not select its host.</summary>
    private static bool UsesFrameworkHost(string project)
    {
        try
        {
            using var reader = XmlReader.Create(project, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            var root = XDocument.Load(reader).Root;
            return root is not null && root.Attribute("Sdk") is null && !root.Elements("Import").Attributes("Sdk").Any() &&
                !root.Elements("Sdk").Any() && !root.Descendants("PropertyGroup").Elements("TargetFramework").Any() &&
                !root.Descendants("PropertyGroup").Elements("TargetFrameworks").Any();
        }
        catch (Exception error) when (error is IOException or XmlException) { return false; }
    }
}
