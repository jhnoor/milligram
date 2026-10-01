using Milligram.Application;
using Milligram.Domain.Model;
using Milligram.Domain.Policies;

namespace Milligram.Analysis.CSharp;

/// <summary>Uses the same policy selection for first-run discovery, commands and live refreshes.</summary>
public sealed class ConfiguredScanner(ILanguageScanner source, IProjectLocator locator,
    Func<IReadOnlyList<string>, string?, ILanguageScanner> projectScanner) : ILanguageScanner
{
    private IScanInputs? inputs;
    public IScanInputs? Inputs => Volatile.Read(ref inputs);

    public CodeModel Scan(ScanRequest request, Action<string>? progress = null)
    {
        var settings = request.Scan;
        ILanguageScanner next;
        IReadOnlyList<string> discovered = [];
        if (settings.Mode == ScanMode.SourceOnly)
        {
            progress?.Invoke("Using the approximate source-only scanner (scan.mode: sourceOnly).");
            next = source;
        }
        else
        {
            var files = settings.Projects.Count > 0 ? settings.Projects : Discover(request, locator.Files(request.Root));
            discovered = files;
            if (files.Count == 0)
            {
                if (settings.Mode == ScanMode.Msbuild) throw new MilligramException("No C# projects were selected. Set scan.projects or scan.mode to sourceOnly in milligram.json.");
                progress?.Invoke("No C# projects in the selected source tree; using the approximate source-only scanner.");
                next = source;
            }
            else next = projectScanner(files, settings.Configuration);
        }
        CodeModel model;
        try { model = next.Scan(request, progress); }
        catch
        {
            if (next.Inputs is not null) Observe();
            throw;
        }
        Observe();
        return model;

        void Observe() => Volatile.Write(ref inputs, next.Inputs is { } observed && settings.Projects.Count == 0
            ? new DiscoveryInputs(observed, request, locator, discovered) : next.Inputs);
    }

    internal static IReadOnlyList<string> Discover(ScanRequest request, IReadOnlyList<string> projects)
    {
        var paths = new ProjectPaths(request.Root);
        var src = Path.GetFullPath(request.SourceDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return projects.Where(project =>
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(project))!.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var related = directory == src || directory.StartsWith(src + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                src.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal);
            var relative = paths.Relative(project);
            return related && !request.Exclude.Any(pattern => Glob.Matches(pattern, relative));
        }).Select(paths.Relative).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
    }

    private sealed class DiscoveryInputs(IScanInputs files, ScanRequest request, IProjectLocator locator, IReadOnlyList<string> projects) : IScanInputs
    {
        public string Version { get; } = files.Version + "\0" + string.Join('\0', projects);
        public string? ReadVersion()
        {
            try
            {
                return files.ReadVersion() is { } version ? version + "\0" + string.Join('\0', Discover(request, locator.Files(request.Root))) : null;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
        }
    }
}
