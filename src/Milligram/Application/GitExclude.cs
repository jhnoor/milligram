namespace Milligram.Application;

/// <summary>
/// The repository's own exclude file, which git reads like a .gitignore but never commits. Worktrees share their
/// repository's file through its common directory, and submodules keep their own.
/// </summary>
public static class GitExclude
{
    /// <summary>The exclude file for the nearest repository at or above the directory; null outside git.</summary>
    public static string? FileFor(string directory)
    {
        for (var dir = new DirectoryInfo(Path.GetFullPath(directory)); dir is not null; dir = dir.Parent)
        {
            var dotGit = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(dotGit)) return InfoExclude(dotGit);
            if (File.Exists(dotGit)) return LinkedGitDirectory(dotGit) is { } linked ? InfoExclude(linked) : null;
        }
        return null;
    }

    /// <summary>Appends the lines git doesn't already have; true when the file changed.</summary>
    public static bool Ensure(string directory, IReadOnlyList<string> lines)
    {
        if (FileFor(directory) is not { } file) return false;
        try
        {
            var existing = File.Exists(file) ? File.ReadAllLines(file) : [];
            var missing = lines.Where(line => !existing.Contains(line)).ToList();
            if (missing.Count == 0) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var block = (existing.Length > 0 && existing[^1].Length > 0 ? "\n" : "") + "# Milligram\n" + string.Join('\n', missing) + "\n";
            File.AppendAllText(file, block);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new MilligramException($"Could not update {file}: {e.Message}");
        }
    }

    private static string InfoExclude(string gitDirectory)
    {
        var commonFile = Path.Combine(gitDirectory, "commondir");
        var common = File.Exists(commonFile)
            ? Path.GetFullPath(File.ReadAllText(commonFile).Trim(), gitDirectory)
            : gitDirectory;
        return Path.Combine(common, "info", "exclude");
    }

    /// <summary>A worktree or submodule has a `.git` file that names its git directory.</summary>
    private static string? LinkedGitDirectory(string dotGitFile)
    {
        const string prefix = "gitdir:";
        var line = File.ReadLines(dotGitFile).FirstOrDefault() ?? "";
        return line.StartsWith(prefix, StringComparison.Ordinal)
            ? Path.GetFullPath(line[prefix.Length..].Trim(), Path.GetDirectoryName(dotGitFile)!)
            : null;
    }
}
