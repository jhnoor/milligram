using Milligram.Analysis.CSharp;

namespace Milligram.Tests.Analysis;

public class SourceFilesTests
{
    [Theory]
    [InlineData("bin")]
    [InlineData("obj")]
    [InlineData(".git")]
    [InlineData("node_modules")]
    [InlineData(".milligram")]
    [InlineData(".vs")]
    [InlineData(".idea")]
    public void GeneratedAndToolDirectoriesAreNeverScanned(string directory)
    {
        using var project = new TempProject(("src/One.cs", ""), ($"src/{directory}/Generated.cs", ""));

        Assert.Equal([Path.Combine(project.Root, "src", "One.cs")], SourceFiles.Find(project.Root, project.Root, []));
    }

    [Fact]
    public void FilesAreSortedAndExcludedRelativeToTheProject()
    {
        using var project = new TempProject(("src/Z.cs", ""), ("src/deep/A.cs", ""), ("src/bin/Generated.cs", ""),
            ("src/deep/Excluded.cs", ""), ("src/Notes.txt", ""), ("src/.custom/Hidden.cs", ""));

        var files = SourceFiles.Find(Path.Combine(project.Root, "src"), project.Root, ["**/Excluded.cs"]);

        Assert.Equal(["src/.custom/Hidden.cs", "src/Z.cs", "src/deep/A.cs"], files.Select(file => SourceFiles.Relative(project.Root, file)));
        Assert.Empty(SourceFiles.Find(Path.Combine(project.Root, "missing"), project.Root, []));
    }

    [UnixFact]
    public void DirectoryLinksDoNotRepeatOrImportSourceTrees()
    {
        using var project = new TempProject(("src/One.cs", ""), ("elsewhere/Other.cs", ""));
        var src = Path.Combine(project.Root, "src");
        Directory.CreateSymbolicLink(Path.Combine(src, "cycle"), src);
        Directory.CreateSymbolicLink(Path.Combine(src, "other"), Path.Combine(project.Root, "elsewhere"));
        Directory.CreateSymbolicLink(Path.Combine(src, "missing"), Path.Combine(project.Root, "missing"));

        Assert.Equal([Path.Combine(src, "One.cs")], SourceFiles.Find(src, project.Root, []));
    }

    [UnixFact]
    public void AnExplicitLinkedSourceRootCanStillBeScanned()
    {
        using var project = new TempProject(("actual/One.cs", ""));
        var linked = Path.Combine(project.Root, "linked");
        Directory.CreateSymbolicLink(linked, Path.Combine(project.Root, "actual"));

        Assert.Equal([Path.Combine(linked, "One.cs")], SourceFiles.Find(linked, project.Root, []));
    }
}
