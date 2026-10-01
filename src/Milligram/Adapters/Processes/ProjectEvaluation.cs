using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.MSBuild;
using Milligram.Analysis.CSharp;

namespace Milligram.Adapters.Processes;

public sealed partial class ProcessRunner
{
    private static readonly Lock msbuildGate = new();

    /// <summary>Roslyn owns the design-time build hosts; their workspace is disposed after every explicit project scan.</summary>
    public async Task<IReadOnlyList<ProjectCompilation>> EvaluateProjectsAsync(
        IReadOnlyList<string> files, string? configuration, Action<string> report)
    {
        RegisterMsBuild(Path.GetDirectoryName(files[0])!, report);
        if (LegacyHostPathWarning(Path.GetDirectoryName(typeof(MSBuildWorkspace).Assembly.Location)!, OperatingSystem.IsWindows()) is { } warning)
            report(warning);
        var properties = new Dictionary<string, string>();
        if (configuration is not null) properties["Configuration"] = configuration;
        using var workspace = MSBuildWorkspace.Create(properties);
        workspace.LoadMetadataForReferencedProjects = false;
        workspace.SkipUnrecognizedProjects = false;
        var validated = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (workspace.CurrentSolution.Projects.Any(project => string.Equals(project.FilePath, file, StringComparison.Ordinal))) continue;
            await ValidateProjectAsync(file, configuration).ConfigureAwait(false);
            validated.Add(file);
            report($"Evaluating {file}" + (configuration is null ? " (project default configuration)." : $" ({configuration})."));
            await workspace.OpenProjectAsync(file).ConfigureAwait(false);
        }
        var result = new List<ProjectCompilation>();
        foreach (var project in workspace.CurrentSolution.Projects.OrderBy(project => project.FilePath, StringComparer.Ordinal).ThenBy(project => project.Name, StringComparer.Ordinal))
        {
            if (project.FilePath is { } path && validated.Add(path)) await ValidateProjectAsync(path, configuration).ConfigureAwait(false);
            if (project.FilePath is null || await project.GetCompilationAsync().ConfigureAwait(false) is not CSharpCompilation compilation)
                throw new InvalidOperationException($"Cannot obtain a C# compilation for {project.Name}.");
            var errors = compilation.GetDeclarationDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).Take(11).ToList();
            foreach (var error in errors.Take(10)) report($"Binding {project.Name}: {error}");
            if (errors.Count > 10) report($"Binding {project.Name}: additional errors omitted.");
            if (errors.Count > 0) report($"{project.Name} has incomplete bindings; fix the reported source errors or missing packages/framework reference assemblies. Its evaluated source membership is retained.");
            result.Add(new ProjectCompilation(project.FilePath, project.Name, compilation));
        }
        foreach (var diagnostic in workspace.Diagnostics) report($"MSBuild {diagnostic.Kind}: {diagnostic.Message}");
        return result;
    }

    /// <summary>Roslyn deliberately ignores missing imports; require normal evaluation before accepting its compiler inputs.</summary>
    private async Task ValidateProjectAsync(string file, string? configuration)
    {
        var args = new List<string> { "msbuild", file, "-nologo", "-verbosity:quiet", "-getProperty:MSBuildProjectFullPath" };
        if (configuration is not null) args.Add("-property:Configuration=" + EscapeProperty(configuration));
        var lines = new Queue<string>();
        var code = await RunAsync("dotnet", args, Path.GetDirectoryName(file)!, line =>
        {
            lock (lines)
            {
                if (lines.Count == 10) lines.Dequeue();
                lines.Enqueue(line.Length > 1024 ? line[..1024] : line);
            }
        }, CancellationToken.None).ConfigureAwait(false);
        if (code != 0) throw new InvalidOperationException($"Evaluation of {file} exited {code}: {string.Join(Environment.NewLine, lines)}");
    }

    internal static string EscapeProperty(string value) => System.Text.RegularExpressions.Regex.Replace(value, @"[%$@();,'?*]",
        match => "%" + ((int)match.Value[0]).ToString("X2", System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>The legacy CLR may fail before emitting any output when its executable configuration exceeds MAX_PATH.</summary>
    internal static string? LegacyHostPathWarning(string directory, bool windows)
    {
        var config = Path.Combine(directory, "BuildHost-net472", "Microsoft.CodeAnalysis.Workspaces.MSBuild.BuildHost.exe.config");
        return windows && config.Length >= 260
            ? $"Legacy MSBuild host configuration path exceeds the Windows .NET Framework limit ({config.Length} characters): {config}. Install Milligram under a shorter --tool-path before evaluating old-style projects; otherwise the host can time out before connecting."
            : null;
    }

    /// <summary>Register before any MSBuild assemblies load, using the SDK selected for the examined project.</summary>
    private static void RegisterMsBuild(string directory, Action<string> report)
    {
        lock (msbuildGate)
        {
            if (MSBuildLocator.IsRegistered) return;
            var instance = MSBuildLocator.QueryVisualStudioInstances(new VisualStudioInstanceQueryOptions
            {
                WorkingDirectory = directory,
                DiscoveryTypes = DiscoveryType.DotNetSdk,
            }).FirstOrDefault() ?? throw new InvalidOperationException("No compatible .NET SDK was found for the examined project.");
            MSBuildLocator.RegisterInstance(instance);
            report($"MSBuild SDK: {instance.MSBuildPath}");
        }
    }
}
