using Milligram.Adapters.Companion;
using Milligram.Adapters.Files;
using Milligram.Application;

namespace Milligram.Tests.Adapters;

public class PhysicalProjectRootTests
{
    [Fact]
    public void RelativePathsAndTrailingSeparatorsHaveOneIdentity()
    {
        using var project = new TempProject();
        var root = Path.Combine(project.Root, "Project & 漢字");
        Directory.CreateDirectory(root);
        var physical = PhysicalProjectRoot.Resolve(root);
        Assert.Equal(physical, PhysicalProjectRoot.Resolve(Path.GetRelativePath(Environment.CurrentDirectory, root)));
        Assert.Equal(physical, PhysicalProjectRoot.Resolve(root + Path.DirectorySeparatorChar));
        Assert.Equal(TmuxCompanion.SessionNameFor(root), TmuxCompanion.SessionNameFor(root + Path.DirectorySeparatorChar));
        Assert.True(Directory.Exists(physical));
    }

    [Fact]
    public void SessionNamesAreShellSafeAndDistinguishRootsWithTheSameReadableName()
    {
        using var project = new TempProject();
        var first = Path.Combine(project.Root, "Project-123 & 漢字");
        var second = Path.Combine(project.Root, "Project-123 + 漢字");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        var name = TmuxCompanion.SessionNameFor(first);
        Assert.Matches("^milligram-Project-123---漢字-[a-f0-9]{8}$", name);
        Assert.NotEqual(name, TmuxCompanion.SessionNameFor(second));
    }

    [Fact]
    public void MissingRootsAndOrdinaryFilesCannotIdentifyAnAgent()
    {
        using var project = new TempProject(("file", "ordinary file"));
        foreach (var name in new[] { "missing", "file" })
        {
            var root = Path.Combine(project.Root, name);
            Assert.Contains(root, Assert.Throws<MilligramException>(() => PhysicalProjectRoot.Resolve(root)).Message);
        }
    }

    [WindowsFact]
    public void WindowsUsesStoredDirectoryCasingInsteadOfTheCallersSpelling()
    {
        using var project = new TempProject();
        var root = Path.Combine(project.Root, "Mixed Case 漢字");
        Directory.CreateDirectory(root);
        Assert.Equal(PhysicalProjectRoot.Resolve(root), PhysicalProjectRoot.Resolve(root.ToUpperInvariant()));
        Assert.Equal(AgentHostFiles.EndpointFor(root), AgentHostFiles.EndpointFor(root.ToUpperInvariant()));
        Assert.Equal(TmuxCompanion.SessionNameFor(root), TmuxCompanion.SessionNameFor(root.ToUpperInvariant()));
    }

    [UnixFact]
    public void LinkedRootsAndAncestorsShareBothAgentNamesAndTheSameLease()
    {
        using var project = new TempProject();
        var root = Path.Combine(project.Root, "actual", "Project 漢字");
        Directory.CreateDirectory(root);
        var direct = Path.Combine(project.Root, "different-name");
        Directory.CreateSymbolicLink(direct, root);
        var ancestor = Path.Combine(project.Root, "ancestor");
        Directory.CreateSymbolicLink(ancestor, Path.Combine(project.Root, "actual"));
        var chain = Path.Combine(project.Root, "chain");
        Directory.CreateSymbolicLink(chain, "different-name");
        var paths = new ProjectPaths(root);
        var files = new AgentHostFiles(paths);
        using var lease = new AgentHostLease(files);
        foreach (var alias in new[] { direct, Path.Combine(ancestor, "Project 漢字"), chain })
        {
            var aliased = new ProjectPaths(alias);
            Assert.Equal(alias, aliased.Root);
            Assert.Equal(PhysicalProjectRoot.Resolve(root), PhysicalProjectRoot.Resolve(alias));
            var aliasFiles = new AgentHostFiles(aliased);
            Assert.Equal(files.Endpoint, aliasFiles.Endpoint);
            Assert.Equal(TmuxCompanion.SessionNameFor(root), TmuxCompanion.SessionNameFor(alias));
            Assert.Throws<IOException>(() => new AgentHostLease(aliasFiles));
        }
        var cached = new AgentHostFiles(new ProjectPaths(chain));
        Directory.Delete(chain);
        Assert.Equal(files.Endpoint, cached.Endpoint);
    }

    [UnixFact]
    public void LinkCyclesAndBrokenLinksCannotBecomeAProjectIdentity()
    {
        using var project = new TempProject();
        var first = Path.Combine(project.Root, "first");
        var second = Path.Combine(project.Root, "second");
        Directory.CreateSymbolicLink(first, second);
        Directory.CreateSymbolicLink(second, first);
        Assert.Throws<MilligramException>(() => PhysicalProjectRoot.Resolve(first));
        Directory.Delete(second);
        Assert.Throws<MilligramException>(() => PhysicalProjectRoot.Resolve(first));
    }

    [UnixFact]
    public void DistinctCaseSensitiveDirectoriesRemainDistinct()
    {
        using var project = new TempProject();
        var upper = Path.Combine(project.Root, "Project");
        var lower = Path.Combine(project.Root, "project");
        Directory.CreateDirectory(upper);
        Directory.CreateDirectory(lower);
        File.WriteAllText(Path.Combine(upper, "identity"), "upper");
        var sameDirectory = File.Exists(Path.Combine(lower, "identity"));
        Assert.Equal(sameDirectory, AgentHostFiles.EndpointFor(upper) == AgentHostFiles.EndpointFor(lower));
        Assert.Equal(sameDirectory, TmuxCompanion.SessionNameFor(upper) == TmuxCompanion.SessionNameFor(lower));
    }
}
