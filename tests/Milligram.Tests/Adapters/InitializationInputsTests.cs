using Milligram.Adapters.Files;
using Milligram.Application;

namespace Milligram.Tests.Adapters;

public class InitializationInputsTests
{
    [Theory]
    [InlineData("src/Order.cs")]
    [InlineData("src/Order.CS")]
    [InlineData("App.csproj")]
    [InlineData("App.CSPROJ")]
    [InlineData("packages.config")]
    [InlineData("Directory.Build.props")]
    [InlineData("Directory.Build.targets")]
    [InlineData("obj/project.assets.json")]
    [InlineData("OBJ/PROJECT.ASSETS.JSON")]
    [InlineData("obj/Debug/net10.0/App.GlobalUsings.g.cs")]
    public void SameLengthChangesWithRestoredTimestampsCannotReuseTheScan(string relative)
    {
        using var project = new TempProject((relative, "before"));
        var paths = new ProjectPaths(project.Root);
        var file = paths.Absolute(relative);
        var written = File.GetLastWriteTimeUtc(file);
        var before = InitializationInputs.Fingerprint(paths);
        using var inputs = new InitializationInputs(paths, watch: false);
        Assert.True(inputs.IsCurrent(), inputs.InvalidationReason);

        project.Write(relative, "after!");
        File.SetLastWriteTimeUtc(file, written);

        Assert.NotEqual(before, InitializationInputs.Fingerprint(paths));
        Assert.False(inputs.IsCurrent());
    }

    [Theory]
    [InlineData("add")]
    [InlineData("delete")]
    [InlineData("rename")]
    [InlineData("move-folder")]
    public void ChangedSourceMembershipCannotReuseTheScan(string change)
    {
        using var project = new TempProject(("src/Old/Order.cs", "class Order { }"));
        var paths = new ProjectPaths(project.Root);
        var before = InitializationInputs.Fingerprint(paths);
        using var inputs = new InitializationInputs(paths, watch: false);

        if (change == "add") project.Write("src/New.cs", "class New { }");
        else if (change == "delete") File.Delete(paths.Absolute("src/Old/Order.cs"));
        else if (change == "rename") File.Move(paths.Absolute("src/Old/Order.cs"), paths.Absolute("src/Old/Renamed.cs"));
        else Directory.Move(paths.Absolute("src/Old"), paths.Absolute("src/New"));

        Assert.NotEqual(before, InitializationInputs.Fingerprint(paths));
        Assert.False(inputs.IsCurrent());
    }

    [Theory]
    [InlineData("edit")]
    [InlineData("rename-file")]
    [InlineData("rename-directory")]
    public void ChangesThatReturnToTheirOriginalContentAreStillObserved(string change)
    {
        using var project = new TempProject(("src/Domain/Order.cs", "class Order { }"));
        var paths = new ProjectPaths(project.Root);
        using var inputs = new InitializationInputs(paths);

        if (change == "edit")
        {
            project.Write("src/Domain/Order.cs", "class Changed { }");
            project.Write("src/Domain/Order.cs", "class Order { }");
        }
        else if (change == "rename-file")
        {
            File.Move(paths.Absolute("src/Domain/Order.cs"), paths.Absolute("src/Domain/Renamed.cs"));
            File.Move(paths.Absolute("src/Domain/Renamed.cs"), paths.Absolute("src/Domain/Order.cs"));
        }
        else
        {
            Directory.Move(paths.Absolute("src/Domain"), paths.Absolute("src/Renamed"));
            Directory.Move(paths.Absolute("src/Renamed"), paths.Absolute("src/Domain"));
        }

        Assert.True(SpinWait.SpinUntil(() => !inputs.IsCurrent(), TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void InitializationAndIgnoredBuildFilesDoNotInvalidateTheSource()
    {
        using var project = new TempProject(("src/Order.cs", "class Order { }"),
            ("obj/Debug/AssemblyInfo.cs", "before"));
        var paths = new ProjectPaths(project.Root);
        var before = InitializationInputs.Fingerprint(paths);
        using var inputs = new InitializationInputs(paths, watch: false);
        Assert.True(inputs.IsCurrent(), inputs.InvalidationReason);

        Write("milligram.json", "{}");
        Write(".gitignore", ".milligram/");
        Write(".milligram/model.json", "{}");
        Write(".milligram/ignored.cs", "class Ignored { }");
        Write("bin/Debug/ignored.cs", "class Ignored { }");
        Write("obj/Debug/AssemblyInfo.cs", "changed");
        Write("notes.md", "notes");
        foreach (var directory in new[] { ".git", "node_modules", ".vs", ".idea" })
        {
            Write(directory + "/ignored.cs", "class Ignored { }");
            inputs.Changed(paths.Absolute(directory), WatcherChangeTypes.Created, directory: true);
        }

        Assert.Equal(before, InitializationInputs.Fingerprint(paths));
        Assert.True(inputs.IsCurrent(), inputs.InvalidationReason);
        inputs.Dispose();
        Assert.False(inputs.IsCurrent());

        void Write(string relative, string text)
        {
            project.Write(relative, text);
            inputs.Changed(paths.Absolute(relative), WatcherChangeTypes.Created);
            inputs.Changed(paths.Absolute(relative), WatcherChangeTypes.Changed);
        }
    }

    [Theory]
    [InlineData(WatcherChangeTypes.Changed)]
    [InlineData(WatcherChangeTypes.Created)]
    [InlineData(WatcherChangeTypes.Deleted)]
    [InlineData(WatcherChangeTypes.Renamed)]
    public void ASourceNotificationRequiresAFreshScanEvenWhenTheContentStillMatches(WatcherChangeTypes change)
    {
        using var project = new TempProject(("App.csproj", "before"));
        var paths = new ProjectPaths(project.Root);
        var before = InitializationInputs.Fingerprint(paths);
        using var inputs = new InitializationInputs(paths, watch: false);
        Assert.True(inputs.IsCurrent(), inputs.InvalidationReason);

        inputs.Changed(paths.Absolute("App.csproj"), change);

        Assert.Equal(before, InitializationInputs.Fingerprint(paths));
        Assert.False(inputs.IsCurrent());
        Assert.Contains("App.csproj", inputs.InvalidationReason);
    }

    [Fact]
    public void CreatingTheConventionalSourceRootInvalidatesTheInitialSelection()
    {
        using var project = new TempProject(("Order.cs", "class Order { }"));
        var paths = new ProjectPaths(project.Root);
        var before = InitializationInputs.Fingerprint(paths);
        using var inputs = new InitializationInputs(paths);

        Directory.CreateDirectory(paths.Absolute("src"));

        Assert.NotEqual(before, InitializationInputs.Fingerprint(paths));
        Assert.False(inputs.IsCurrent());
    }

    [WindowsFact]
    public void AnInputThatCannotBeReadRequiresAFreshScan()
    {
        using var project = new TempProject(("Order.cs", "class Order { }"));
        var paths = new ProjectPaths(project.Root);
        using var inputs = new InitializationInputs(paths);
        using var locked = new FileStream(paths.Absolute("Order.cs"), FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.Null(InitializationInputs.Fingerprint(paths));
        Assert.False(inputs.IsCurrent());
    }

    [UnixFact]
    public void AnExternalFileLinkDetectsContentChangesWithoutWatchingTheTargetDirectory()
    {
        using var project = new TempProject();
        using var source = new TempProject(("Order.cs", "class Order { }"));
        File.CreateSymbolicLink(Path.Combine(project.Root, "Order.cs"), Path.Combine(source.Root, "Order.cs"));
        var paths = new ProjectPaths(project.Root);
        using var inputs = new InitializationInputs(paths, watch: false);
        Assert.True(inputs.IsCurrent(), inputs.InvalidationReason);

        source.Write("Order.cs", "class Other { }");

        Assert.False(inputs.IsCurrent());
    }

    [Fact]
    public void GeneratedImportTimestampsAffectWhichFileTheScannerSelects()
    {
        using var project = new TempProject(("obj/Debug/App.GlobalUsings.g.cs", "global using System;"),
            ("obj/Release/App.GlobalUsings.g.cs", "global using System.Net;"));
        var paths = new ProjectPaths(project.Root);
        var file = paths.Absolute("obj/Debug/App.GlobalUsings.g.cs");
        var before = InitializationInputs.Fingerprint(paths);

        File.SetLastWriteTimeUtc(file, File.GetLastWriteTimeUtc(file).AddMinutes(1));

        Assert.NotEqual(before, InitializationInputs.Fingerprint(paths));
    }

    [UnixFact]
    public void ALinkedConventionalSourceRootRequiresAFreshScan()
    {
        using var project = new TempProject();
        using var source = new TempProject(("Order.cs", "class Order { }"));
        Directory.CreateSymbolicLink(Path.Combine(project.Root, "src"), source.Root);
        var paths = new ProjectPaths(project.Root);
        using var inputs = new InitializationInputs(paths);

        Assert.Null(InitializationInputs.Fingerprint(paths));
        Assert.False(inputs.IsCurrent());
    }
}
