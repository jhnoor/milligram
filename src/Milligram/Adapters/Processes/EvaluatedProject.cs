namespace Milligram.Adapters.Processes;

/// <summary>Matches compiler workspaces to evaluated settings without guessing ownership from source folders.</summary>
internal sealed record EvaluatedProject(string Path, string? Framework, string? Configuration, string Output)
{
    public static EvaluatedProject? Find(IEnumerable<EvaluatedProject> projects, string path, string name, string? output)
    {
        var candidates = projects.Where(project => string.Equals(project.Path, path, StringComparison.Ordinal)).Distinct().ToList();
        var matches = candidates.Where(project => string.Equals(project.Output, output, StringComparison.Ordinal)).ToList();
        if (matches.Count == 1) return matches[0];
        // Roslyn 5.9 names multiple contexts FileName(TargetFramework), even when their output paths coincide.
        var named = matches.Where(project => string.Equals(name,
            System.IO.Path.GetFileNameWithoutExtension(path) + "(" + project.Framework + ")", StringComparison.Ordinal)).ToList();
        return named.Count == 1 ? named[0] : null;
    }
}
