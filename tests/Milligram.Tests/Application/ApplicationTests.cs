using System.Text.Json.Nodes;
using Milligram.Adapters.Cli;
using Milligram.Analysis.CSharp;
using Milligram.Application;
using Milligram.Domain.Hierarchy;
using Milligram.Domain.Metrics;
using Milligram.Domain.Model;
using Milligram.Domain.Policies;

namespace Milligram.Tests.Application;

public class PolicyJsonTests
{
    [Fact]
    public void ReadsCommentsTrailingCommasAndBothProposalEntryShapes()
    {
        using var project = new TempProject(("milligram.json", """
            {
              // a comment
              "prefix": "Shop",
              "levels": [["Domain"], ["Web"],],
              "edgeKinds": [{ "from": "A", "to": "B", "kind": "association" }],
              "proposals": [{
                "id": "p1", "name": "Split",
                "layers": [{ "id": "core", "label": "Core", "namespaces": ["Domain", { "id": "io", "label": "IO", "namespaces": ["Web"] }] }]
              }]
            }
            """));

        var policy = JsonFile.Read<Policy>(Path.Combine(project.Root, "milligram.json"))!;

        Assert.Equal("Shop", policy.Prefix);
        Assert.Equal(2, policy.Levels.Count);
        Assert.Equal(Milligram.Domain.Model.EdgeKind.Association, policy.EdgeKinds[0].Kind);
        var entries = policy.Proposals[0].Layers[0].Namespaces;
        Assert.Equal("Domain", entries[0].Namespace);
        Assert.Equal("IO", entries[1].Group!.Label);
        Assert.Equal(Policy.DefaultExclude, policy.Exclude);
    }

    [Fact]
    public void ProposalEntriesRoundTrip()
    {
        using var project = new TempProject();
        var path = Path.Combine(project.Root, "p.json");
        var policy = new Policy
        {
            Proposals = [new Proposal { Id = "p", Layers = [new ProposalGroup { Id = "g", Namespaces = [ProposalEntry.Of("A"), ProposalEntry.Of(new ProposalGroup { Id = "n" })] }] }],
        };
        JsonFile.Write(path, policy);
        var text = File.ReadAllText(path);
        Assert.Contains("\"A\"", text);
        var read = JsonFile.Read<Policy>(path)!;
        Assert.Equal("n", read.Proposals[0].Layers[0].Namespaces[1].Group!.Id);
    }
}

public class JsonFileTests
{
    [Fact]
    public void AnIoFailureWhileStreamingIsReportedAndKeepsTheOldFile()
    {
        using var project = new TempProject(("state.json", "old"));
        var path = Path.Combine(project.Root, "state.json");

        IEnumerable<string> Values()
        {
            yield return "partial";
            throw new IOException("Source read failed.");
        }

        Assert.Equal("Source read failed.", Assert.Throws<IOException>(() => JsonFile.Write(path, Values())).Message);
        Assert.Equal("old", File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFiles(project.Root));
    }

    [Fact]
    public void AFailedCreateIsNotMistakenForAnotherWritersFile()
    {
        using var project = new TempProject();
        var path = Path.Combine(project.Root, "state.json");
        Directory.CreateDirectory(path);

        var error = Record.Exception(() => JsonFile.TryCreateText(path, "new"));

        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.Empty(Directory.GetFiles(project.Root));
        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public void ConcurrentCreatorsPublishExactlyOneCompleteFile()
    {
        using var project = new TempProject();
        var path = Path.Combine(project.Root, "state.json");
        var created = new System.Collections.Concurrent.ConcurrentBag<int>();

        Parallel.For(0, 8, i =>
        {
            if (JsonFile.TryCreateText(path, $"writer-{i}")) created.Add(i);
        });

        Assert.Equal($"writer-{Assert.Single(created)}", File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFiles(project.Root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TypedWritesReachDiskBeforeFinishingEnumerationAndKeepTheOldFileUntilSuccess(bool fail)
    {
        using var project = new TempProject(("state.json", "old"));
        var path = Path.Combine(project.Root, "state.json");
        var chunk = new string('x', 4096);
        var streamed = false;

        IEnumerable<string> Values()
        {
            for (var i = 0; i < 64; i++) yield return chunk;
            Assert.Equal("old", File.ReadAllText(path));
            var temporary = Directory.GetFiles(project.Root, "*.tmp");
            streamed = temporary.Length == 1 && new FileInfo(temporary[0]).Length > 0;
            if (fail) throw new InvalidOperationException("Enumeration failed.");
            yield return "last";
        }

        if (fail)
        {
            var error = Assert.Throws<InvalidOperationException>(() => JsonFile.Write(path, Values()));
            Assert.Equal("Enumeration failed.", error.Message);
            Assert.Equal("old", File.ReadAllText(path));
        }
        else
        {
            JsonFile.Write(path, Values());
            var saved = JsonFile.Read<string[]>(path)!;
            Assert.Equal(65, saved.Length);
            Assert.All(saved.Take(64), value => Assert.Equal(chunk, value));
            Assert.Equal("last", saved[^1]);
        }

        Assert.True(streamed, "The temporary file should receive chunks before the source is fully enumerated.");
        Assert.Equal([path], Directory.GetFiles(project.Root));
    }

    [Fact]
    public void TypedWritesKeepTheExistingUtf8FormatOptionsAndFinalNewline()
    {
        using var project = new TempProject();
        var path = Path.Combine(project.Root, "nested", "state.json");
        var value = new { Name = "Grüße 漢字 <Tag> + \"quoted\"", Kind = MemberKind.TopLevel, Missing = (string?)null };
        var expected = System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(value, MilligramJson.Options) + "\n");

        JsonFile.Write(path, value);

        Assert.Equal(expected, File.ReadAllBytes(path));
        Assert.Equal([path], Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Theory]
    [InlineData(null, "null\n")]
    [InlineData("", "\"\"\n")]
    public void TypedWritesPreserveNullAndEmptyValues(string? value, string expected)
    {
        using var project = new TempProject();
        var path = Path.Combine(project.Root, "state.json");

        JsonFile.Write(path, value);

        Assert.Equal(expected, File.ReadAllText(path));
    }

    [WindowsFact]
    public void ATypedWriteWaitsForAReaderBeforeReplacingTheFile()
    {
        using var project = new TempProject(("state.json", "old"));
        var path = Path.Combine(project.Root, "state.json");
        var release = ReleaseSoon(HoldOpen(path));

        JsonFile.Write(path, new[] { "new" });

        release.Join();
        Assert.Equal(["new"], JsonFile.Read<string[]>(path)!);
        Assert.Equal([path], Directory.GetFiles(project.Root));
    }

    [WindowsFact]
    public void AFailedTypedReplacementKeepsTheOldFileAndRemovesTheTemporaryFile()
    {
        using var project = new TempProject(("state.json", "old"));
        var path = Path.Combine(project.Root, "state.json");
        using (HoldOpen(path))
        {
            var refused = Assert.Throws<MilligramException>(() => JsonFile.Write(path, new[] { "new" }));
            Assert.Contains(path, refused.Message);
        }

        Assert.Equal("old", File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFiles(project.Root));
    }

    /// <summary>Opens a file the way a virus scanner or an editor might: reading, and sharing neither writes nor deletes.</summary>
    private static FileStream HoldOpen(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read);

    /// <summary>
    /// Lets go of the file after 200 ms on a thread of its own. A thread-pool timer can fire a second late while
    /// parallel scans in other tests keep the pool busy, which is past the retries and made these tests flaky.
    /// </summary>
    private static Thread ReleaseSoon(FileStream held)
    {
        var thread = new Thread(() =>
        {
            Thread.Sleep(200);
            held.Dispose();
        })
        { IsBackground = true };
        thread.Start();
        return thread;
    }

    [Fact]
    public void AWriteReplacesTheFileAndLeavesNoTemporaryFileBehind()
    {
        using var project = new TempProject(("state/a.json", "old"));
        var path = Path.Combine(project.Root, "state", "a.json");

        JsonFile.WriteText(path, "new");

        Assert.Equal("new", File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [WindowsFact]
    public void AWriteWaitsBrieflyForAReaderToLetGo()
    {
        using var project = new TempProject(("a.json", "old"));
        var path = Path.Combine(project.Root, "a.json");
        // Even a reader that shares deletes, as Milligram's own do, blocks a rename over the file on Windows.
        var release = ReleaseSoon(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));

        JsonFile.WriteText(path, "new");

        release.Join();
        Assert.Equal("new", JsonFile.ReadText(path));
        Assert.Equal([path], Directory.GetFiles(project.Root));
    }

    [WindowsFact]
    public void AReplaceWaitsBrieflyForAScannerToLetGoOfTheNewFile()
    {
        using var project = new TempProject(("a.json", "old"), ("a.json.new.tmp", "new"));
        var path = Path.Combine(project.Root, "a.json");
        // A scanner reading the new file makes the rename fail with a sharing violation, not access denied.
        var release = ReleaseSoon(HoldOpen(path + ".new.tmp"));

        JsonFile.Replace(path + ".new.tmp", path);

        release.Join();
        Assert.Equal("new", File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFiles(project.Root));
    }

    [WindowsFact]
    public void AFileThatStaysOpenIsReportedByNameAndKeepsItsContents()
    {
        using var project = new TempProject(("a.json", "old"));
        var path = Path.Combine(project.Root, "a.json");

        using (HoldOpen(path))
        {
            var refused = Assert.Throws<MilligramException>(() => JsonFile.WriteText(path, "new"));
            Assert.Contains(path, refused.Message);
        }

        Assert.Equal("old", File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFiles(project.Root));
    }
}

public class MailboxTests
{
    [Fact]
    public void MessagesAreTakenOldestFirstAndRemoved()
    {
        using var project = new TempProject();
        var mailbox = new Mailbox(Path.Combine(project.Root, "mail"));
        mailbox.Post("context", new JsonObject { ["context"] = "real" });
        Thread.Sleep(5);
        mailbox.Post("message", new JsonObject { ["text"] = "hello" });

        Assert.Equal(2, mailbox.Count);
        var peeked = mailbox.Take(keep: true);
        Assert.Equal(["context", "message"], peeked.Select(m => m.Op));
        Assert.Equal("hello", (string?)peeked[1].Data["text"]);
        Assert.Equal(2, mailbox.Take().Count);
        Assert.Empty(mailbox.Take());
    }

    [Fact]
    public void HandWrittenMailWithoutAnEnvelopeIsAccepted()
    {
        using var project = new TempProject(("mail/001.json", """{ "op": "display", "context": "p1" }"""), ("mail/002.json", "not json"));
        var messages = new Mailbox(Path.Combine(project.Root, "mail")).Take();
        Assert.Equal("display", messages[0].Op);
        Assert.Equal("p1", (string?)messages[0].Data["context"]);
        Assert.Equal("invalid", messages[1].Op);
    }

    [Fact]
    public void AMissingMailboxIsEmpty() => Assert.Empty(new Mailbox("/definitely/not/here").Take());
}

public class ScanSummaryTests
{
    private const string PolicyJson = """{ "prefix": "Shop", "levels": [["Domain"], ["Web"]] }""";

    private static Workspace Scanned(TempProject project)
    {
        var workspace = new Workspace(new ProjectPaths(project.Root), new CSharpScanner());
        workspace.Load();
        workspace.Generate();
        return workspace;
    }

    [Fact]
    public void CountsTypesNamespacesAndRedArrowsAndAsksForCrapFirst()
    {
        using var project = new TempProject(
            ("milligram.json", PolicyJson),
            ("A.cs", """
                namespace Shop.Domain { public class Order { Shop.Web.Page? page; } }
                namespace Shop.Web { public class Page { } }
                """));

        var summary = ScanSummary.Of(Scanned(project));

        Assert.Equal(new ScanSummary(2, 2, 1, HasCrap: false, HasMutation: false), summary);
        Assert.Equal(
            ["2 types in 2 namespaces, 1 red arrow.", "Next: `milligram crap` to colour the boxes by complexity × missing coverage (runs your tests)."],
            summary.Describe());
    }

    [Fact]
    public void ACleanDiagramSaysSoAndMovesOnToMutation()
    {
        var summary = new ScanSummary(9, 3, 0, HasCrap: true, HasMutation: false);
        Assert.Equal(
            ["9 types in 3 namespaces, no red arrows.", "Next: `milligram mutate` to score how well those tests kill mutants."],
            summary.Describe());
    }

    [Fact]
    public void WithBothMetricsItPointsAtTheWorstThingOnScreen()
    {
        Assert.Equal("Next: open the viewer and follow the red arrows: they point the wrong way.",
            new ScanSummary(9, 3, 2, HasCrap: true, HasMutation: true).Describe().Last());
        Assert.Equal("Next: open the viewer and start with the reddest boxes.",
            new ScanSummary(9, 3, 0, HasCrap: true, HasMutation: true).Describe().Last());
    }

    /// <summary>The commonest first-run failure: "src" points somewhere with no C# in it.</summary>
    [Fact]
    public void AnEmptyScanSendsTheUserBackToTheConfiguration()
    {
        using var project = new TempProject(("milligram.json", """{ "src": "nope" }"""));
        var summary = ScanSummary.Of(Scanned(project));

        Assert.Equal(new ScanSummary(0, 0, 0, HasCrap: false, HasMutation: false), summary);
        Assert.Equal(["No types found. Check \"src\" and \"exclude\" in milligram.json, then rerun."], summary.Describe());
    }
}

public class WorkspaceTests
{
    private const string Source = """
        namespace Shop.Domain { public class Order { } }
        namespace Shop.Web { public class Page { Shop.Domain.Order? order; } }
        """;

    private static Workspace Open(TempProject project)
    {
        var workspace = new Workspace(new ProjectPaths(project.Root), new CSharpScanner());
        workspace.Load();
        return workspace;
    }

    [Fact]
    public void GenerateScansWritesTheModelAndBuildsViews()
    {
        using var project = new TempProject(("milligram.json", """{ "prefix": "Shop", "levels": [["Domain"], ["Web"]] }"""), ("A.cs", Source));
        var workspace = Open(project);

        var model = workspace.Generate();

        Assert.True(File.Exists(workspace.Paths.ModelFile));
        Assert.Equal(2, model.Types.Count);
        var view = workspace.View(null, null);
        Assert.Equal(["ns:Domain", "ns:Web"], view.Nodes.Where(n => n.Parent is null).Select(n => n.Id));
        Assert.Contains(view.Edges, e => e.From == "t:Shop.Web.Page" && e.To == "t:Shop.Domain.Order");
        Assert.Equal(["A.cs"], workspace.FilesOf(null, "ns:Web"));
        Assert.Equal("Order", workspace.Card(null, "Shop.Domain.Order")!.Name);
    }

    [Fact]
    public void MetricsSnapshotsAreWrittenInKeyOrder()
    {
        using var project = new TempProject(("milligram.json", """{ "prefix": "Shop" }"""));
        var workspace = Open(project);
        var members = new Dictionary<string, Milligram.Domain.Metrics.MutationEntry>
        {
            ["b"] = new(1, 0, 0, 0, "h"),
            ["a"] = new(1, 0, 0, 0, "h"),
            ["C"] = new(1, 0, 0, 0, "h"),
        };
        var files = new Dictionary<string, DateTimeOffset> { ["z.cs"] = DateTimeOffset.UnixEpoch, ["m.cs"] = DateTimeOffset.UnixEpoch };

        workspace.SaveMetrics(new Milligram.Domain.Metrics.MutationSnapshot(DateTimeOffset.UnixEpoch, members, files));

        var saved = JsonFile.Read<Milligram.Domain.Metrics.MutationSnapshot>(workspace.Paths.MutationFile)!;
        Assert.Equal(["C", "a", "b"], saved.Members.Keys);
        Assert.Equal(["m.cs", "z.cs"], saved.Files.Keys);
        Assert.Equal(["C", "a", "b"], workspace.Metrics.Mutation.Members.Keys);
    }

    [Fact]
    public void MutationGapDetailsRoundTripInStableOrder()
    {
        using var project = new TempProject();
        var workspace = Open(project);
        var first = new Mutant("a.cs", 2, 3, MutantStatus.Survived, "Equality") { EndLine = 2, EndColumn = 5, Replacement = ">=" };
        var second = first with { Line = 3, EndLine = 3, Replacement = "<" };
        Mutant[] ordered =
        [
            first with { File = "A.cs" }, first, first with { EndColumn = 6 }, first with { EndLine = 3 },
            first with { Status = MutantStatus.NoCoverage }, first with { Replacement = "z" },
            first with { Mutator = "Other" }, first with { Column = 4 }, second, first with { File = "z.cs" },
        ];
        var snapshot = MutationSnapshot.Empty with
        {
            Gaps = new Dictionary<string, IReadOnlyList<Mutant>> { ["z"] = ordered.Reverse().ToList(), ["A"] = [second] },
        };

        workspace.SaveMetrics(snapshot);
        var written = File.ReadAllText(workspace.Paths.MutationFile);
        workspace.ReloadMetrics();

        Assert.Equal(["A", "z"], workspace.Metrics.Mutation.Gaps.Keys);
        Assert.Equal(ordered, workspace.Metrics.Mutation.Gaps["z"]);
        workspace.SaveMetrics(snapshot with { Gaps = new Dictionary<string, IReadOnlyList<Mutant>> { ["A"] = [second], ["z"] = ordered } });
        Assert.Equal(written, File.ReadAllText(workspace.Paths.MutationFile));
    }

    [Fact]
    public void OldMutationSnapshotsLoadWithoutGapDetails()
    {
        using var project = new TempProject((".milligram/metrics/mutation.json", """
            { "generatedAt": "2026-01-01T00:00:00Z", "members": {
              "App.A.M()": { "killed": 1, "timeout": 0, "survived": 2, "uncovered": 0, "hash": "h" }
            }, "files": { "a.cs": "2026-01-01T00:00:00Z" } }
            """));

        var workspace = Open(project);

        Assert.Equal(2, workspace.Metrics.Mutation.Members["App.A.M()"].Survived);
        Assert.Empty(workspace.Metrics.Mutation.Gaps);
    }

    [Fact]
    public void ABrokenPolicyKeepsTheLastGoodOne()
    {
        using var project = new TempProject(("milligram.json", """{ "prefix": "Shop" }"""));
        var workspace = Open(project);
        File.WriteAllText(workspace.Paths.PolicyFile, "{ nope");

        Assert.False(workspace.ReloadPolicy());
        Assert.Equal("Shop", workspace.Policy.Prefix);
        Assert.StartsWith("milligram.json:", workspace.PolicyError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnOlderReloadCannotOverwriteAViewerEditOrItsClearedError(bool fails)
    {
        using var project = new TempProject(("milligram.json", """{ "title": "Original" }"""));
        var workspace = Open(project);
        using var reading = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var editing = new ManualResetEventSlim();
        var reloading = Task.Factory.StartNew(() => workspace.ReloadPolicy(() =>
        {
            var loaded = JsonFile.Read<Policy>(workspace.Paths.PolicyFile);
            reading.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Reload was not released.");
            if (fails) throw new IOException("An older read failed.");
            return loaded;
        }), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task? edit = null;
        try
        {
            Assert.True(reading.Wait(TimeSpan.FromSeconds(5)));
            edit = Task.Factory.StartNew(() =>
            {
                editing.Set();
                workspace.EditPolicy(policy => policy with { Title = "Edited" });
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.True(editing.Wait(TimeSpan.FromSeconds(5)));
            Assert.NotSame(edit, await Task.WhenAny(edit, Task.Delay(100)));
        }
        finally
        {
            release.Set();
            await reloading.WaitAsync(TimeSpan.FromSeconds(5));
            if (edit is not null) await edit.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(!fails, await reloading);
        Assert.Equal("Edited", workspace.Policy.Title);
        Assert.Equal("Edited", JsonFile.Read<Policy>(workspace.Paths.PolicyFile)!.Title);
        Assert.Null(workspace.PolicyError);
    }

    [Fact]
    public void PolicyEditsAreWrittenAndVisible()
    {
        using var project = new TempProject(("milligram.json", """{ "prefix": "Shop" }"""), ("A.cs", Source));
        var workspace = Open(project);
        workspace.Generate();
        var editor = new PolicyEditor(workspace);

        var proposal = editor.NewProposal("Split");
        editor.Omit("Web", proposal.Id);
        editor.Omit("Domain", null);
        editor.RenameProposal(proposal.Id, "Split v2");

        var reread = JsonFile.Read<Policy>(workspace.Paths.PolicyFile)!;
        Assert.Equal("Split v2", reread.Proposals.Single().Name);
        Assert.Equal(["Web"], reread.Proposals[0].Omit);
        Assert.Equal(["Domain"], reread.Omit);
        Assert.True(workspace.Tree(proposal.Id).IsProposal);
        Assert.False(workspace.Tree(DiagramTree.RealContext).Contains("Shop.Domain.Order"));

        editor.DeleteProposal(proposal.Id);
        Assert.Empty(workspace.Policy.Proposals);
    }

    [Fact]
    public void ViewerEditsKeepHandWrittenCommentsAndLayout()
    {
        const string text = """
            {
              // Shop, as we mean it to be.
              "prefix": "Shop",
              "levels": [ ["Domain"], ["Web"] ] // inner first
            }

            """;
        using var project = new TempProject(("milligram.json", text), ("A.cs", Source));
        var workspace = Open(project);
        workspace.Generate();
        Assert.True(workspace.Tree(null).Contains("Shop.Web.Page"));
        var editor = new PolicyEditor(workspace);

        editor.Omit("Web", null);
        editor.NewProposal("Split");

        Assert.False(workspace.Tree(null).Contains("Shop.Web.Page"));
        var written = File.ReadAllText(workspace.Paths.PolicyFile);
        Assert.StartsWith("""
            {
              // Shop, as we mean it to be.
              "prefix": "Shop",
              "levels": [ ["Domain"], ["Web"] ], // inner first
              "omit": ["Web"],
            """, written);
        var reread = JsonFile.Read<Policy>(workspace.Paths.PolicyFile)!;
        Assert.Equal(["Web"], reread.Omit);
        Assert.Equal("Split", reread.Proposals.Single().Name);
    }

    [Fact]
    public void ViewerEditsWithoutAFileWriteOnlyWhatDiffersFromTheDefaults()
    {
        using var project = new TempProject(("A.cs", Source));
        var workspace = Open(project);

        new PolicyEditor(workspace).Omit("Web", null);

        Assert.Equal("{\n  \"omit\": [\"Web\"]\n}\n", File.ReadAllText(workspace.Paths.PolicyFile));
    }

    [Fact]
    public void AViewerEditAfterTheFileWasDeletedWritesTheWholePolicy()
    {
        using var project = new TempProject(("milligram.json", """{ "prefix": "Shop" }"""));
        var workspace = Open(project);
        File.Delete(workspace.Paths.PolicyFile);

        new PolicyEditor(workspace).Omit("Web", null);

        Assert.Equal("{\n  \"prefix\": \"Shop\",\n  \"omit\": [\"Web\"]\n}\n", File.ReadAllText(workspace.Paths.PolicyFile));
    }

    [Fact]
    public void AViewerEditIsRefusedWhileTheFileIsBroken()
    {
        using var project = new TempProject(("milligram.json", """{ "prefix": "Shop" }"""));
        var workspace = Open(project);
        File.WriteAllText(workspace.Paths.PolicyFile, "{ \"prefix\": ");

        var refused = Assert.Throws<MilligramException>(() => new PolicyEditor(workspace).Omit("Web", null));
        Assert.StartsWith("Fix milligram.json before editing it from the viewer:", refused.Message);
        Assert.Equal("{ \"prefix\": ", File.ReadAllText(workspace.Paths.PolicyFile));
        Assert.Empty(workspace.Policy.Omit);
    }
}

public class ProjectInitializerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InitialScanReportsProgressBeforeWritingThePolicy(bool force)
    {
        using var project = new TempProject(("A.cs", "namespace Shop; public class A { }"));
        var paths = new ProjectPaths(project.Root);
        var progress = new List<string>();
        var initializer = new ProjectInitializer(paths, new CSharpScanner(), new FakeProjectLocator());

        Assert.NotNull(initializer.Initialize(force, line =>
        {
            Assert.False(File.Exists(paths.PolicyFile));
            progress.Add(line);
        }));

        Assert.Equal("Scanning 1 source file(s).", progress[0]);
        Assert.Contains("Parsed 1/1 files.", progress);
        Assert.Contains("Bound 1/1 files.", progress);
        Assert.Contains("Linked 1/1 types.", progress);
        Assert.StartsWith("Scanned 1 types and 0 dependencies in ", progress[^1]);
        Assert.True(File.Exists(paths.PolicyFile));
    }

    [Fact]
    public void ExistingPolicySkipsInitializationAndScanProgress()
    {
        using var project = new TempProject(("milligram.json", "{ \"prefix\": \"Kept\" }"));
        var initializer = new ProjectInitializer(new ProjectPaths(project.Root), new CSharpScanner(), new FakeProjectLocator());
        var progress = new List<string>();

        Assert.Null(initializer.Initialize(force: false, progress.Add));

        Assert.Empty(progress);
        Assert.Equal("{ \"prefix\": \"Kept\" }", File.ReadAllText(Path.Combine(project.Root, "milligram.json")));
    }

    [Fact]
    public void ATestProjectAtTheRootDoesNotContributeProductionNamespaces()
    {
        using var project = new TempProject(
            ("Tests.csproj", "<Project><ItemGroup><PackageReference Include=\"xunit\" /></ItemGroup></Project>"),
            ("Tests.cs", "namespace Shop.Tests; public class Example { }"));
        var initializer = new ProjectInitializer(new ProjectPaths(project.Root), new CSharpScanner(), new Milligram.Analysis.DotNet.DotNetProjectLocator());

        var policy = initializer.Propose().Policy;

        Assert.Equal("", policy.Prefix);
        Assert.Empty(policy.Levels);
        Assert.Contains("**", policy.Exclude);
    }

    [Fact]
    public void GlobalTypesDoNotHideTheCommonNamespacePrefix()
    {
        using var project = new TempProject(
            ("Global.cs", "public class Global { }"),
            ("Domain.cs", "namespace Shop.Domain; public class Order { }"),
            ("Web.cs", "namespace Shop.Web; public class Page { }"));
        var initializer = new ProjectInitializer(new ProjectPaths(project.Root), new CSharpScanner(), new FakeProjectLocator());

        Assert.Equal("Shop", initializer.Propose().Policy.Prefix);
    }

    [Fact]
    public void InitialPolicyIncludesUsedLibrariesAndTheNextScanDrawsTheirOvals()
    {
        using var project = new TempProject(("A.cs", """
            namespace Shop;
            class A
            {
                Microsoft.CodeAnalysis.SyntaxTree? syntax;
                Microsoft.AspNetCore.Http.HttpContext? context;
                string Save() => System.Text.Json.JsonSerializer.Serialize(new System.Collections.Generic.List<int>());
            }
            """));
        var paths = new ProjectPaths(project.Root);
        var initialization = new ProjectInitializer(paths, new CSharpScanner(), new FakeProjectLocator()).Initialize(force: false)!;
        string[] expected = ["Microsoft.AspNetCore", "Microsoft.CodeAnalysis", "System.Text.Json"];
        Assert.Equal(expected, initialization.Policy.Foreign);
        Assert.Equal(expected, JsonFile.Read<Policy>(paths.PolicyFile)!.Foreign);
        Assert.Equal("Library ovals, inferred from usage (edit foreign in milligram.json): Microsoft.AspNetCore, Microsoft.CodeAnalysis, System.Text.Json.", initialization.Describe()[0]);

        var workspace = new Workspace(paths, new CSharpScanner());
        workspace.Load();
        var rescanned = workspace.Generate();
        Assert.Equal(expected, rescanned.Foreign.Select(n => n.Label));
        var initial = Assert.IsType<CodeModel>(initialization.Model);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(rescanned with { GeneratedAt = initial.GeneratedAt }, MilligramJson.Options),
            System.Text.Json.JsonSerializer.Serialize(initial, MilligramJson.Options));
    }

    [Fact]
    public void APolicyCreatedDuringTheScanIsNotOverwritten()
    {
        using var project = new TempProject(("A.cs", "namespace Shop; public class A { }"));
        var paths = new ProjectPaths(project.Root);
        var initializer = new ProjectInitializer(paths, new CSharpScanner(), new FakeProjectLocator());

        var initialization = initializer.Initialize(force: false, _ => project.Write("milligram.json", "{ \"title\": \"User choice\" }"));

        Assert.Null(initialization);
        Assert.Equal("User choice", JsonFile.Read<Policy>(paths.PolicyFile)!.Title);
    }

    [Fact]
    public void LibrarySuggestionsAreDescribedEvenWithoutLayers()
    {
        Assert.Equal(["Library ovals, inferred from usage (edit foreign in milligram.json): System.Text.Json."],
            new Initialization(new Policy { Foreign = ["System.Text.Json"] }, []).Describe());
    }

    [Theory]
    [InlineData(new[] { "Shop.Domain", "Shop.Web.Pages" }, "Shop")]
    [InlineData(new[] { "Shop.Domain.Model", "Shop.Domain.Rules" }, "Shop.Domain")]
    [InlineData(new[] { "Shop", "Other" }, "")]
    [InlineData(new string[0], "")]
    public void CommonPrefixIsTheSharedLeadingSegments(string[] namespaces, string expected) =>
        Assert.Equal(expected, ProjectInitializer.CommonPrefix(namespaces));

    [Fact]
    public void InitializeWritesAPolicyFromTheSourceAndIgnoresRunFiles()
    {
        using var project = new TempProject(
            ("src/Shop/Domain/Order.cs", "namespace Shop.Domain; public class Order { }"),
            ("src/Shop/Web/Page.cs", "namespace Shop.Web; public class Page { public Shop.Domain.Order Order = new(); }"),
            ("tests/Shop.Tests/Shop.Tests.csproj", "<Project><ItemGroup><PackageReference Include=\"xunit\" /></ItemGroup></Project>"),
            ("tests/Shop.Tests/T.cs", "namespace Shop.Tests; public class T { }"));
        var paths = new ProjectPaths(project.Root);
        var initializer = new ProjectInitializer(paths, new CSharpScanner(), new Milligram.Analysis.DotNet.DotNetProjectLocator());

        Assert.NotNull(initializer.Initialize(force: false));
        Assert.Null(initializer.Initialize(force: false));

        var policy = JsonFile.Read<Policy>(paths.PolicyFile)!;
        Assert.Equal("src", policy.Src);
        Assert.Equal("Shop", policy.Prefix);
        Assert.Equal(["Web", "Domain"], policy.Order);
        Assert.Equal([["Domain"], ["Web"]], policy.Levels);
        Assert.Contains("tests/Shop.Tests/**", policy.Exclude);
        Assert.Contains(".milligram/", File.ReadAllLines(Path.Combine(project.Root, ".gitignore")));
    }

    [Fact]
    public void InitializeWritesAShortCommentedFileThatLoadsAsTheProposedPolicy()
    {
        using var project = new TempProject(
            ("src/Shop/Domain/Order.cs", "namespace Shop.Domain; public class Order { }"),
            ("src/Shop/Web/Page.cs", "namespace Shop.Web; public class Page { public Shop.Domain.Order Order = new(); }"));
        var paths = new ProjectPaths(project.Root);
        var initializer = new ProjectInitializer(paths, new CSharpScanner(), new FakeProjectLocator());

        var proposed = initializer.Initialize(force: false)!.Policy;

        var text = File.ReadAllText(paths.PolicyFile);
        Assert.True(text.Split('\n').Length <= 22, text);
        Assert.Contains("// Inferred from the dependencies", text);
        Assert.Contains("\"levels\": [\n    [\"Domain\"],\n    [\"Web\"]\n  ],", text);
        Assert.Equal(PolicyTextTests.Json(proposed), PolicyTextTests.Json(JsonFile.Read<Policy>(paths.PolicyFile)!));
    }

    [Fact]
    public void InitializeReportsTheLightestLinksOfCyclesAsOutward()
    {
        using var project = new TempProject(
            ("Domain.cs", "namespace Shop.Domain { public class Order { public Shop.Web.Page? Back; } }"),
            ("Web.cs", "namespace Shop.Web { public class Page { public Shop.Domain.Order A = new(), B = new(); public Shop.Domain.Order C() => A; } }"));
        var initializer = new ProjectInitializer(new ProjectPaths(project.Root), new CSharpScanner(), new FakeProjectLocator());

        var initialization = initializer.Initialize(force: false)!;

        Assert.Equal([["Domain"], ["Web"]], initialization.Policy.Levels);
        var outward = Assert.Single(initialization.Outward);
        Assert.Equal(("Domain", "Web", 1), (outward.From, outward.To, outward.Count));
        Assert.Equal(
            [
                "Levels, inferred from the dependencies (inner first; edit them in milligram.json):",
                "  L0  Domain",
                "  L1  Web",
                "1 dependency points outward (red), the lightest links in dependency cycles:",
                "  Domain -> Web (1 reference)",
            ],
            initialization.Describe());
    }

    private static DependencyEdge Outward(string from, string to, int count) => new(from, to, EdgeKind.Dependency, count);

    [Fact]
    public void DescribeListsLevelsAndTheLightestOutwardLinksFirst()
    {
        var described = new Initialization(
            new Policy { Levels = [["Domain"], ["Adapters", "Analysis"]] },
            [Outward("B", "Z", 5), Outward("C", "Y", 1), Outward("A", "Z", 5), Outward("A", "Y", 5)]).Describe();

        Assert.Equal(
            [
                "Levels, inferred from the dependencies (inner first; edit them in milligram.json):",
                "  L0  Domain",
                "  L1  Adapters, Analysis",
                "4 dependencies point outward (red), the lightest links in dependency cycles:",
                "  C -> Y (1 reference)",
                "  A -> Y (5 references)",
                "  A -> Z (5 references)",
                "  B -> Z (5 references)",
            ],
            described);
    }

    [Fact]
    public void DescribeSaysSoWhenThereAreNoCyclesAndCapsLongLists()
    {
        Assert.Empty(new Initialization(new Policy(), []).Describe());
        var acyclic = new Initialization(new Policy { Levels = [["Domain"]] }, []);
        Assert.Equal("No dependency cycles between top-level namespaces.", acyclic.Describe()[^1]);

        var ten = Enumerable.Range(0, 10).Select(i => Outward($"N{i:00}", "Top", 2)).ToList();
        Assert.Equal("  N09 -> Top (2 references)", new Initialization(acyclic.Policy, ten).Describe()[^1]);

        var twelve = Enumerable.Range(0, 12).Select(i => Outward($"N{i:00}", "Top", 2)).ToList();
        var described = new Initialization(acyclic.Policy, twelve).Describe();
        Assert.Contains("12 dependencies point outward (red), the lightest links in dependency cycles:", described);
        Assert.DoesNotContain("  N10 -> Top (2 references)", described);
        Assert.Equal("  and 2 more", described[^1]);
    }
}

public class PolicyTextTests
{
    internal static string Json(Policy policy) => System.Text.Json.JsonSerializer.Serialize(policy, MilligramJson.Compact);

    private static Policy Parse(string text) => System.Text.Json.JsonSerializer.Deserialize<Policy>(text, MilligramJson.Options)!;

    [Fact]
    public void AStarterPutsListsThatDoNotFitOnePerLine()
    {
        var order = Enumerable.Range(0, 20).Select(i => $"Namespace{i:00}").ToList();
        var policy = new Policy { Title = "big", Src = "src", Prefix = "Big", Order = order, Levels = [["Namespace00"]] };

        var text = PolicyText.Starter(new Initialization(policy, []));

        Assert.Contains("\"order\": [\n    \"Namespace00\",\n    \"Namespace01\",", text);
        Assert.Contains("    \"Namespace19\"\n  ],", text);
        Assert.Equal(Json(policy), Json(Parse(text)));
    }

    [Fact]
    public void AStarterWithNothingToLayerStillLoads()
    {
        var policy = new Policy();

        var text = PolicyText.Starter(new Initialization(policy, []));

        Assert.Contains("\"title\": null,", text);
        Assert.Contains("\"levels\": [],", text);
        Assert.Equal(Json(policy), Json(Parse(text)));
    }

    [Fact]
    public void AnEditRewritesOnlyTheKeysThatChanged()
    {
        const string text = """
            {
              // keep me
              "prefix": "Shop", // and me
              "omit": [ "A" ],
              /* block */ "levels": [["Domain"]]
            }
            """;
        var before = Parse(text);

        var edited = PolicyText.Edit(text, before, before with { Omit = ["A", "B"] });

        Assert.Equal(text.Replace("[ \"A\" ]", "[\"A\", \"B\"]"), edited);
    }

    [Fact]
    public void AnEditFindsKeysWhateverTheirCase()
    {
        const string text = "{ \"Omit\": [\"A\"] }";
        var before = Parse(text);

        Assert.Equal("{ \"Omit\": [\"A\", \"B\"] }", PolicyText.Edit(text, before, before with { Omit = ["A", "B"] }));
    }

    [Theory]
    [InlineData("{ \"prefix\": \"Shop\" }", "{ \"prefix\": \"Shop\", \n  \"omit\": [\"Web\"]\n}")]
    [InlineData("{\n  \"prefix\": \"Shop\",\n}\n", "{\n  \"prefix\": \"Shop\",\n  \"omit\": [\"Web\"]\n}\n")]
    [InlineData("{\n  \"prefix\": \"Shop\" // a comment, with a comma\n}\n", "{\n  \"prefix\": \"Shop\", // a comment, with a comma\n  \"omit\": [\"Web\"]\n}\n")]
    [InlineData("{\n  \"prefix\": \"Shop\" /* a, b */ ,\n}\n", "{\n  \"prefix\": \"Shop\" /* a, b */ ,\n  \"omit\": [\"Web\"]\n}\n")]
    [InlineData("{\n  \"prefix\": \"Shop\" , /* x */\n}\n", "{\n  \"prefix\": \"Shop\" , /* x */\n  \"omit\": [\"Web\"]\n}\n")]
    [InlineData("{\n  \"prefix\": \"Shop\" // a note\n  ,\n}\n", "{\n  \"prefix\": \"Shop\" // a note\n  ,\n  \"omit\": [\"Web\"]\n}\n")]
    [InlineData("{\n\"prefix\": \"Shop\"\n}\n", "{\n\"prefix\": \"Shop\",\n\"omit\": [\"Web\"]\n}\n")]
    [InlineData("{\n    \"prefix\": \"Shop\"\n}\n", "{\n    \"prefix\": \"Shop\",\n    \"omit\": [\"Web\"]\n}\n")]
    [InlineData("{}", "{\n  \"omit\": [\"Web\"]\n}")]
    public void AnEditAppendsAKeyTheTextLacks(string text, string expected)
    {
        var before = Parse(text);

        var edited = PolicyText.Edit(text, before, before with { Omit = ["Web"] });

        Assert.Equal(expected, edited);
        Assert.Equal(["Web"], Parse(edited).Omit);
    }

    [Fact]
    public void AnEditLaysOutLongValuesOnePerLineAtTheKeysIndent()
    {
        const string text = "{\n    \"proposals\": []\n}\n";
        var proposal = new Proposal
        {
            Id = "p1",
            Name = "Split the core",
            Layers = [new ProposalGroup { Id = "core", Label = "Core", Namespaces = [ProposalEntry.Of("Domain"), ProposalEntry.Of("Application")] }],
        };
        var before = Parse(text);

        var edited = PolicyText.Edit(text, before, before with { Proposals = [proposal] });

        Assert.Equal(
            """
            {
                "proposals": [
                  {
                    "id": "p1",
                    "name": "Split the core",
                    "layers": [{ "id": "core", "label": "Core", "namespaces": ["Domain", "Application"] }],
                    "omit": []
                  }
                ]
            }

            """,
            edited);
        Assert.Equal(Json(before with { Proposals = [proposal] }), Json(Parse(edited)));
    }

    [Theory]
    [InlineData("[1]", "milligram.json is not a JSON object.")]
    [InlineData("{ \"prefix\": ", null)]
    public void AnEditRefusesTextThatIsNotAJsonObject(string text, string? message)
    {
        var refused = Assert.ThrowsAny<System.Text.Json.JsonException>(() => PolicyText.Edit(text, new Policy(), new Policy { Omit = ["Web"] }));
        if (message is not null) Assert.Equal(message, refused.Message);
    }
}

public class CommandLineTests
{
    [Fact]
    public void DefaultsToServe() => Assert.Equal("serve", CommandLine.Parse([]).Command);

    [Fact]
    public void ParsesCommandsFlagsOptionsAndArguments()
    {
        var line = CommandLine.Parse(["mutate", "--all", "a.cs", "--port", "6000", "b.cs", "--coverage=x.xml", "--coverage", "y.xml"]);
        Assert.Equal("mutate", line.Command);
        Assert.True(line.Has("all"));
        Assert.Equal(["a.cs", "b.cs"], line.Arguments);
        Assert.Equal(6000, line.Port(1));
        Assert.Equal(["x.xml", "y.xml"], line.Values("coverage"));
        Assert.Equal(7, CommandLine.Parse([]).Port(7));
    }
}
