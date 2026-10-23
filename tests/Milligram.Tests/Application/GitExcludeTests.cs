using Milligram.Application;

namespace Milligram.Tests.Application;

/// <summary>
/// Repositories sit one folder below the temp project, so a mutant that walks past one writes inside the temp
/// project instead of the shared parent every other test lives under.
/// </summary>
public class GitExcludeTests
{
    [Fact]
    public void ARepositoryKeepsItsExcludeFileInItsGitDirectory()
    {
        using var project = new TempProject(("repo/src/App/A.cs", ""));
        var repo = Path.Combine(project.Root, "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));

        Assert.Equal(Path.Combine(repo, ".git", "info", "exclude"), GitExclude.FileFor(repo));
        Assert.Equal(Path.Combine(repo, ".git", "info", "exclude"), GitExclude.FileFor(Path.Combine(repo, "src", "App")));
    }

    [Fact]
    public void AWorktreeSharesItsRepositorysExcludeFile()
    {
        using var project = new TempProject(
            ("main/.git/worktrees/feature/commondir", "../..\n"),
            ("feature/.git", "gitdir: ../main/.git/worktrees/feature\n"));

        Assert.Equal(Path.Combine(project.Root, "main", ".git", "info", "exclude"), GitExclude.FileFor(Path.Combine(project.Root, "feature")));
    }

    [Fact]
    public void ASubmoduleKeepsItsOwnExcludeFile()
    {
        using var project = new TempProject(
            ("repo/.git/modules/lib/HEAD", "ref: refs/heads/main\n"),
            ("repo/lib/.git", "gitdir: ../.git/modules/lib\n"));
        var repo = Path.Combine(project.Root, "repo");

        Assert.Equal(Path.Combine(repo, ".git", "modules", "lib", "info", "exclude"), GitExclude.FileFor(Path.Combine(repo, "lib")));
    }

    [Theory]
    [InlineData("not a git link\n")]
    [InlineData("")]
    public void AGitFileWithoutADirectoryIsNotFollowed(string text)
    {
        using var project = new TempProject(("repo/.git", text));
        var repo = Path.Combine(project.Root, "repo");

        Assert.Null(GitExclude.FileFor(repo));
        Assert.False(GitExclude.Ensure(repo, [".milligram/"]));
    }

    [Fact]
    public void OutsideGitNothingIsWritten()
    {
        using var project = new TempProject(("A.cs", ""));

        Assert.Null(GitExclude.FileFor(project.Root));
        Assert.False(GitExclude.Ensure(project.Root, [".milligram/"]));
        Assert.Equal(["A.cs"], Directory.GetFileSystemEntries(project.Root).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("# local\n*.log", "# local\n*.log\n# Milligram\n.milligram/\n")]
    [InlineData("# local\n*.log\n", "# local\n*.log\n\n# Milligram\n.milligram/\n")]
    [InlineData("# local\n*.log\n\n", "# local\n*.log\n\n# Milligram\n.milligram/\n")]
    public void OnlyMissingLinesAreAppendedAfterWhatIsThere(string before, string after)
    {
        using var project = new TempProject(("repo/.git/info/exclude", before));
        var repo = Path.Combine(project.Root, "repo");

        Assert.True(GitExclude.Ensure(repo, ["*.log", ".milligram/"]));
        Assert.False(GitExclude.Ensure(repo, ["*.log", ".milligram/"]));

        Assert.Equal(after, File.ReadAllText(Path.Combine(repo, ".git", "info", "exclude")));
    }

    [Fact]
    public void TheInfoDirectoryIsCreatedWhenGitHasNone()
    {
        using var project = new TempProject(("repo/A.cs", ""));
        var repo = Path.Combine(project.Root, "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));

        Assert.True(GitExclude.Ensure(repo, [".milligram/"]));

        Assert.Equal("# Milligram\n.milligram/\n", File.ReadAllText(Path.Combine(repo, ".git", "info", "exclude")));
    }

    [Fact]
    public void AnUnwritableExcludeFileIsReportedWithItsPath()
    {
        using var project = new TempProject(("repo/A.cs", ""));
        var repo = Path.Combine(project.Root, "repo");
        var file = Path.Combine(repo, ".git", "info", "exclude");
        Directory.CreateDirectory(file);

        var error = Assert.Throws<MilligramException>(() => GitExclude.Ensure(repo, [".milligram/"]));

        Assert.StartsWith($"Could not update {file}: ", error.Message);
    }
}
