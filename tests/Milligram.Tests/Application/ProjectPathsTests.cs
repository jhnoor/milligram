using Milligram.Application;

namespace Milligram.Tests.Application;

public class ProjectPathsTests
{
    [Fact]
    public void ContainsRejectsTraversal()
    {
        var paths = new ProjectPaths("/tmp/project");
        Assert.True(paths.Contains("/tmp/project/src/A.cs"));
        Assert.True(paths.Contains("src/A.cs"));
        Assert.False(paths.Contains("../other/A.cs"));
        Assert.False(paths.Contains("/tmp/project-evil/A.cs"));
        Assert.False(paths.Contains("/etc/passwd"));
        Assert.False(paths.Contains(paths.Root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExistingFilesAndMissingDescendantsStayInsideTheRoot(bool trailingSlash)
    {
        using var project = new TempProject(("src/Inside.cs", "inside"));
        var paths = new ProjectPaths(project.Root + (trailingSlash ? Path.DirectorySeparatorChar : ""));
        Assert.True(paths.Contains("src/Inside.cs"));
        Assert.True(paths.Contains(Path.Combine(project.Root, "src", "Inside.cs")));
        Assert.True(paths.Contains("src/missing/NotYet.cs"));
        Assert.False(paths.Contains("src/../../Outside.cs"));
    }

    [Fact]
    public void AFilesystemRootDoesNotRequireADoubledSeparator()
    {
        using var project = new TempProject(("Inside.cs", "inside"));
        var paths = new ProjectPaths(Path.GetPathRoot(project.Root)!);
        Assert.True(paths.Contains(Path.Combine(project.Root, "Inside.cs")));
    }

    [Fact]
    public void AFilesystemErrorCannotBeMistakenForAMissingContainedPath()
    {
        using var project = new TempProject();
        Assert.False(new ProjectPaths(project.Root).Contains(new string('x', 1024)));
    }

    [UnixFact]
    public void DirectoryAndFileLinksCannotImportFilesFromOutsideTheRoot()
    {
        using var project = new TempProject();
        using var outside = new TempProject(("Outside.cs", "outside"));
        Directory.CreateSymbolicLink(Path.Combine(project.Root, "linked"), outside.Root);
        File.CreateSymbolicLink(Path.Combine(project.Root, "linked.cs"), Path.GetRelativePath(project.Root, Path.Combine(outside.Root, "Outside.cs")));
        var paths = new ProjectPaths(project.Root);
        Assert.False(paths.Contains("linked/Outside.cs"));
        Assert.False(paths.Contains("linked.cs"));
        Assert.False(paths.Contains("linked/Missing.cs"));
    }

    [UnixFact]
    public void AChainCannotLeaveTheProjectAndReturnThroughAnotherLink()
    {
        using var project = new TempProject(("Inside.cs", "inside"));
        using var outside = new TempProject();
        File.CreateSymbolicLink(Path.Combine(outside.Root, "Return.cs"), Path.Combine(project.Root, "Inside.cs"));
        File.CreateSymbolicLink(Path.Combine(project.Root, "return.cs"), Path.Combine(outside.Root, "Return.cs"));
        Directory.CreateSymbolicLink(Path.Combine(outside.Root, "return"), project.Root);
        Directory.CreateSymbolicLink(Path.Combine(project.Root, "return"), Path.Combine(outside.Root, "return"));
        var paths = new ProjectPaths(project.Root);
        foreach (var file in new[] { "return.cs", "return/Inside.cs" })
        {
            Assert.Equal("inside", File.ReadAllText(paths.Absolute(file)));
            Assert.False(paths.Contains(file));
        }
    }

    [UnixFact]
    public void InternalLinksAndChainsRemainUsable()
    {
        using var project = new TempProject(("inside/Target.cs", "inside"));
        Directory.CreateSymbolicLink(Path.Combine(project.Root, "directory"), "inside");
        File.CreateSymbolicLink(Path.Combine(project.Root, "middle.cs"), "directory/Target.cs");
        File.CreateSymbolicLink(Path.Combine(project.Root, "chain.cs"), "middle.cs");
        var paths = new ProjectPaths(project.Root);
        Assert.True(paths.Contains("directory/Target.cs"));
        Assert.True(paths.Contains("chain.cs"));
        Assert.Equal("inside", File.ReadAllText(paths.Absolute("chain.cs")));
    }

    [UnixFact]
    public void AParentSegmentInALinkTargetIsEvaluatedAfterThePrecedingLink()
    {
        using var project = new TempProject(("a/Target.cs", "inside"), ("Target.cs", "decoy"));
        Directory.CreateDirectory(Path.Combine(project.Root, "a", "b"));
        Directory.CreateSymbolicLink(Path.Combine(project.Root, "hop"), "a/b");
        File.CreateSymbolicLink(Path.Combine(project.Root, "parent.cs"), "hop/../Target.cs");
        var paths = new ProjectPaths(project.Root);
        Assert.Equal("inside", File.ReadAllText(paths.Absolute("parent.cs")));
        Assert.True(paths.Contains("parent.cs"));

        using var outside = new TempProject(("Target.cs", "outside"));
        Directory.CreateDirectory(Path.Combine(outside.Root, "deep"));
        Directory.CreateSymbolicLink(Path.Combine(project.Root, "out"), Path.Combine(outside.Root, "deep"));
        File.CreateSymbolicLink(Path.Combine(project.Root, "escape.cs"), "out/../Target.cs");
        Assert.Equal("outside", File.ReadAllText(paths.Absolute("escape.cs")));
        Assert.False(paths.Contains("escape.cs"));
    }

    [UnixFact]
    public void CyclesAreBoundedAndMissingInternalTargetsStillHaveAContainedPath()
    {
        using var project = new TempProject();
        Directory.CreateSymbolicLink(Path.Combine(project.Root, "first"), "second");
        Directory.CreateSymbolicLink(Path.Combine(project.Root, "second"), "first");
        File.CreateSymbolicLink(Path.Combine(project.Root, "missing.cs"), "missing/Target.cs");
        File.CreateSymbolicLink(Path.Combine(project.Root, "outside.cs"), "../missing/Target.cs");
        var paths = new ProjectPaths(project.Root);
        Assert.False(paths.Contains("first/Target.cs"));
        Assert.True(paths.Contains("missing.cs"));
        Assert.False(paths.Contains("outside.cs"));
    }

    [UnixFact]
    public void AnExplicitlyLinkedRootAndLinkedAncestorsRetainTheirOwnBoundary()
    {
        using var project = new TempProject(("actual/project/Inside.cs", "inside"));
        using var outside = new TempProject(("Outside.cs", "outside"));
        var actual = Path.Combine(project.Root, "actual", "project");
        Directory.CreateSymbolicLink(Path.Combine(project.Root, "ancestor"), "actual");
        Directory.CreateSymbolicLink(Path.Combine(project.Root, "direct"), "actual/project");
        Directory.CreateSymbolicLink(Path.Combine(actual, "outside"), outside.Root);
        var roots = new[] { actual, Path.Combine(project.Root, "ancestor", "project"), Path.Combine(project.Root, "direct") };
        for (var i = 0; i < roots.Length; i++)
        {
            var paths = new ProjectPaths(roots[i] + Path.DirectorySeparatorChar);
            File.CreateSymbolicLink(Path.Combine(actual, $"internal{i}.cs"), Path.Combine(roots[i], "Inside.cs"));
            Assert.True(paths.Contains("Inside.cs"));
            Assert.True(paths.Contains($"internal{i}.cs"));
            Assert.False(paths.Contains("outside/Outside.cs"));
        }
    }

    [UnixFact]
    public void APhysicalRootAcceptsAnInternalLinkNamedThroughAnAncestorAlias()
    {
        using var project = new TempProject(("actual/project/Inside.cs", "inside"));
        var actual = Path.Combine(project.Root, "actual", "project");
        Directory.CreateSymbolicLink(Path.Combine(project.Root, "alias"), "actual");
        Directory.CreateSymbolicLink(Path.Combine(project.Root, "chain"), "alias");
        var aliasedRoot = Path.Combine(project.Root, "chain", "project");
        Directory.CreateSymbolicLink(Path.Combine(actual, "cycle"), aliasedRoot);
        File.CreateSymbolicLink(Path.Combine(actual, "internal.cs"), Path.Combine(aliasedRoot, "Inside.cs"));
        var paths = new ProjectPaths(actual);
        Assert.True(paths.Contains("cycle/Inside.cs"));
        Assert.True(paths.Contains("internal.cs"));
        Assert.Equal("inside", File.ReadAllText(paths.Absolute("internal.cs")));

        using var outside = new TempProject();
        Directory.CreateSymbolicLink(Path.Combine(outside.Root, "return"), actual);
        File.CreateSymbolicLink(Path.Combine(actual, "outside.cs"), Path.Combine(outside.Root, "return", "Inside.cs"));
        Assert.False(paths.Contains("outside.cs"));
    }

    [UnixFact]
    public void ALoopInAnAncestorAliasCannotKeepAContainmentCheckRunning()
    {
        using var project = new TempProject(("actual/Inside.cs", "inside"));
        var actual = Path.Combine(project.Root, "actual");
        Directory.CreateSymbolicLink(Path.Combine(project.Root, "first"), "second");
        Directory.CreateSymbolicLink(Path.Combine(project.Root, "second"), "first");
        File.CreateSymbolicLink(Path.Combine(actual, "cycle.cs"), Path.Combine(project.Root, "first", "Inside.cs"));
        Assert.False(new ProjectPaths(actual).Contains("cycle.cs"));
    }

    [UnixFact]
    public void LinkTargetsAreCheckedAgainForEveryRequest()
    {
        using var project = new TempProject(("Inside.cs", "inside"));
        using var outside = new TempProject(("Outside.cs", "outside"));
        var link = Path.Combine(project.Root, "link.cs");
        File.CreateSymbolicLink(link, "Inside.cs");
        var paths = new ProjectPaths(project.Root);
        Assert.True(paths.Contains("link.cs"));
        File.Delete(link);
        File.CreateSymbolicLink(link, Path.Combine(outside.Root, "Outside.cs"));
        Assert.False(paths.Contains("link.cs"));
    }
}
