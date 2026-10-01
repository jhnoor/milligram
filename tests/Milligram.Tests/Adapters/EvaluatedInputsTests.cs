using Milligram.Adapters.Files;
using Milligram.Adapters.Processes;

namespace Milligram.Tests.Adapters;

public class EvaluatedInputsTests
{
    [Fact]
    public void FailedSelectionKeepsWatchingOldInputsAndNewAncestorConfiguration()
    {
        using var project = new TempProject(("old/input.extra", "old"), ("new/App/App.csproj", "<Project/>"));
        var previous = new EvaluatedInputs();
        previous.AddFiles([Path.Combine(project.Root, "old/input.extra")]);
        previous.AddPattern(Path.Combine(project.Root, "old"), "sources", path => path.EndsWith(".cs", StringComparison.Ordinal));
        previous.Complete();
        var next = new EvaluatedInputs();
        next.AddAncestors(Path.Combine(project.Root, "new/App"));
        next.Include(previous);
        next.Complete();
        Assert.Equal(next.Version, next.ReadVersion());
        project.Write("old/Added.cs", "class Added {}");
        Assert.NotEqual(next.Version, next.ReadVersion());
        var afterSource = next.ReadVersion();
        project.Write("old/input.extra", "repaired previous import");
        Assert.NotEqual(afterSource, next.ReadVersion());
        var afterImport = next.ReadVersion();
        project.Write("Directory.Build.props", "<Project/>");
        Assert.NotEqual(afterImport, next.ReadVersion());
    }

    [Theory]
    [InlineData("edit")]
    [InlineData("delete")]
    [InlineData("create")]
    [InlineData("rename")]
    public void ExternalImportsAndWildcardMembershipInvalidateTheSnapshot(string change)
    {
        using var project = new TempProject(("imports/settings.extra", "before"), ("shared/A.cs", "class A {}"));
        var inputs = new EvaluatedInputs();
        inputs.AddFiles([Path.Combine(project.Root, "imports/settings.extra")]);
        inputs.AddDirectories([Path.Combine(project.Root, "shared")]);
        inputs.Complete();
        Assert.Equal(inputs.Version, inputs.ReadVersion());
        if (change == "edit") project.Write("imports/settings.extra", "different settings");
        else if (change == "delete") File.Delete(Path.Combine(project.Root, "imports/settings.extra"));
        else if (change == "rename") File.Move(Path.Combine(project.Root, "shared/A.cs"), Path.Combine(project.Root, "shared/B.cs"));
        else project.Write("shared/B.cs", "class B {}");
        Assert.NotEqual(inputs.Version, inputs.ReadVersion());
    }

    [Fact]
    public void BuildOutputsAreIgnoredExceptForExplicitEvaluatedInputs()
    {
        using var project = new TempProject(("obj/Custom.g.cs", "class Generated {}"));
        var inputs = new EvaluatedInputs();
        inputs.AddDirectories([project.Root]);
        inputs.AddFiles([Path.Combine(project.Root, "obj/Custom.g.cs"), Path.Combine(project.Root, "optional.props")]);
        inputs.Complete();
        project.Write("obj/unrelated.cache", "noise");
        project.Write("bin/output.dll", "noise");
        project.Write(".milligram/model.json", "noise");
        project.Write("milligram.json", "{}");
        Assert.Equal(inputs.Version, inputs.ReadVersion());
        project.Write("obj/Custom.g.cs", "class ChangedGenerated {}");
        Assert.NotEqual(inputs.Version, inputs.ReadVersion());
    }

    [Fact]
    public void ChangesDuringEvaluationAreNotRebaselinedWhenTheSnapshotCompletes()
    {
        using var project = new TempProject(("Settings.props", "before"));
        var inputs = new EvaluatedInputs();
        inputs.AddFiles([Path.Combine(project.Root, "Settings.props")]);
        project.Write("Settings.props", "changed while evaluating");
        inputs.AddFiles([Path.Combine(project.Root, "Settings.props")]);
        inputs.Complete();
        Assert.NotEqual(inputs.Version, inputs.ReadVersion());
    }

    [Fact]
    public void ImportWildcardsIgnoreOtherBuildOutputButNoticeMatchingFilesInNestedOutputFolders()
    {
        using var project = new TempProject(("obj/cache.bin", "before"));
        var inputs = new EvaluatedInputs();
        inputs.AddPattern(Path.Combine(project.Root, "obj"), "imports", path => path.EndsWith(".props", StringComparison.Ordinal));
        inputs.Complete();
        project.Write("obj/cache.bin", "cache rewritten by design time evaluation");
        Assert.Equal(inputs.Version, inputs.ReadVersion());
        project.Write("obj/nested/obj/Generated.props", "<Project/>");
        Assert.NotEqual(inputs.Version, inputs.ReadVersion());
    }

    [Theory]
    [InlineData("../Shared/**/*.cs", "Shared")]
    [InlineData("../Shared\\*.txt", "Shared")]
    [InlineData("*.cs", "App")]
    public void ExpandedWildcardsRetainTheirPhysicalDirectory(string glob, string expected)
    {
        using var project = new TempProject();
        Assert.Equal(Path.GetFullPath(Path.Combine(project.Root, expected)),
            Path.TrimEndingDirectorySeparator(ProjectInputFiles.GlobDirectory(Path.Combine(project.Root, "App"), glob)!));
        Assert.Null(ProjectInputFiles.GlobDirectory(project.Root, "Exact.cs"));
    }

    [Fact]
    public async Task PollingFindsEditsThatPrecedeSubscriptionAndDoesNotRepeatAFailedRefresh()
    {
        using var project = new TempProject(("Settings.props", "before"));
        var inputs = new EvaluatedInputs();
        inputs.AddFiles([Path.Combine(project.Root, "Settings.props")]);
        inputs.Complete();
        project.Write("Settings.props", "already changed");
        var calls = 0;
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var poller = new ScanInputPoller(() => inputs, () => { Interlocked.Increment(ref calls); changed.TrySetResult(); }, TimeSpan.FromMilliseconds(10)))
        {
            await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(100);
            Assert.Equal(1, Volatile.Read(ref calls));
        }
        project.Write("Settings.props", "after dispose");
        await Task.Delay(50);
        Assert.Equal(1, calls);
    }
}
