using System.Runtime.CompilerServices;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Globbing;
using Milligram.Adapters.Files;

namespace Milligram.Adapters.Processes;

/// <summary>Reads actual evaluated imports and wildcard roots; XML guesses cannot describe conditional project inputs.</summary>
internal static class ProjectInputFiles
{
    private static readonly HashSet<string> FileItems = ["Compile", "AdditionalFiles", "Analyzer", "EditorConfigFiles", "GlobalAnalyzerConfigFiles",
        "ProjectReference", "EmbeddedResource", "Content", "None", "ReferencePath", "Page", "ApplicationDefinition"];

    // Loading this method must happen after MSBuildLocator registers the examined project's SDK.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Read(string file, string? configuration, EvaluatedInputs inputs, ISet<string> inspected)
    {
        try { Evaluate(file, configuration, inputs, inspected); }
        catch (Microsoft.Build.Exceptions.InvalidProjectFileException error)
        {
            if (error.ProjectFile is { } path) inputs.AddFiles([path]);
            throw new InvalidOperationException($"Cannot track the evaluated inputs of {file}: {error.Message}", error);
        }
    }

    private static void Evaluate(string file, string? configuration, EvaluatedInputs inputs, ISet<string> inspected)
    {
        var properties = new Dictionary<string, string>();
        if (configuration is not null) properties["Configuration"] = configuration;
        using var collection = new ProjectCollection(properties);
        ReadProject(file);
        collection.UnloadAllProjects();

        void ReadProject(string path)
        {
            path = Path.GetFullPath(path);
            if (!inspected.Add(path)) return;
            var project = new Project(path, properties, toolsVersion: null, collection, ProjectLoadSettings.IgnoreMissingImports);
            ReadInputs(project);
            foreach (var framework in project.GetPropertyValue("TargetFrameworks").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var inner = new Dictionary<string, string>(properties) { ["TargetFramework"] = framework };
                ReadInputs(new Project(path, inner, toolsVersion: null, collection, ProjectLoadSettings.IgnoreMissingImports));
            }
        }

        void ReadInputs(Project project)
        {
            Add(project, inputs);
            foreach (var reference in project.GetItems("ProjectReference").Select(item => item.GetMetadataValue("FullPath")))
                if (reference.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) && File.Exists(reference)) ReadProject(reference);
        }
    }

    private static void Add(Project project, EvaluatedInputs inputs)
    {
        inputs.AddFiles([project.FullPath]);
        inputs.AddFiles(project.Imports.Select(import => import.ImportedProject.FullPath));
        foreach (var xml in project.Imports.Select(import => import.ImportedProject).Append(project.Xml))
            foreach (var import in xml.Imports)
            {
                try
                {
                    var directory = Path.GetDirectoryName(xml.FullPath)!;
                    var expression = import.Project.Replace("$(MSBuildThisFileDirectory)", directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                        .Replace("$(MSBuildThisFileFullPath)", xml.FullPath, StringComparison.OrdinalIgnoreCase);
                    foreach (var path in project.ExpandString(expression).Split(';', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (GlobDirectory(directory, path) is { } glob)
                        {
                            var parsed = MSBuildGlob.Parse(directory, path);
                            inputs.AddPattern(glob, "import:" + path, parsed.IsMatch);
                        }
                        else inputs.AddFiles([Path.GetFullPath(Path.Combine(directory, path.Replace('\\', Path.DirectorySeparatorChar)))]);
                    }
                }
                // A false-condition import may contain an invalid path. It must not prevent otherwise valid evaluation.
                catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException) { }
            }
        inputs.AddFiles(project.AllEvaluatedItems.Where(item => FileItems.Contains(item.ItemType)).Select(item => item.GetMetadataValue("FullPath")));
        inputs.AddFiles(project.GetItems("Reference").Select(item => item.GetMetadataValue("HintPath")).Where(path => path.Length > 0)
            .Select(path => Path.GetFullPath(Path.Combine(project.DirectoryPath, path.Replace('\\', Path.DirectorySeparatorChar)))));
        inputs.AddFiles([Path.GetFullPath(Path.Combine(project.DirectoryPath, project.GetPropertyValue("ProjectAssetsFile") is { Length: > 0 } assets ? assets : "obj/project.assets.json"))]);
        foreach (var glob in project.GetAllGlobs().Where(glob => FileItems.Contains(glob.ItemElement.ItemType)))
            foreach (var pattern in glob.IncludeGlobs)
                if (GlobDirectory(project.DirectoryPath, pattern) is { } directory)
                    inputs.AddPattern(directory, "item:" + pattern + ":" + string.Join(';', glob.Excludes) + ":" + string.Join(';', glob.Removes), glob.MsBuildGlob.IsMatch);
        inputs.AddAncestors(project.DirectoryPath);
    }

    internal static string? GlobDirectory(string directory, string pattern)
    {
        var wildcard = pattern.IndexOfAny(['*', '?']);
        if (wildcard < 0) return null;
        var slash = pattern[..wildcard].LastIndexOfAny(['/', '\\']);
        return Path.GetFullPath(Path.Combine(directory, slash < 0 ? "." : pattern[..(slash + 1)].Replace('\\', Path.DirectorySeparatorChar)));
    }
}
