using System.Text.Json.Nodes;
using Milligram.Analysis.CSharp;
using Milligram.Application;
using Milligram.Domain.Policies;

namespace Milligram.Tests.Application;

public class ViewerActionsTests : IDisposable
{
    private const string Policy = """
        {
          "prefix": "Shop",
          "levels": [["Domain"], ["Web"]],
          "proposals": [{ "id": "p1", "name": "Split", "layers": [{ "id": "core", "label": "Core", "namespaces": ["Domain"] }] }]
        }
        """;

    private const string Source = """
        namespace Shop.Domain { public class Order { public int Total() => 1; } }
        namespace Shop.Web { public class Page { public Shop.Domain.Order Order = new(); } }
        """;

    private readonly TempProject project = new(("milligram.json", Policy), ("Shop.cs", Source));
    private readonly Workspace workspace;
    private readonly FakeCompanion companion = new();
    private readonly FakeEvents events = new();
    private readonly JobQueue jobs;
    private readonly ViewerActions actions;

    public ViewerActionsTests()
    {
        workspace = new Workspace(new ProjectPaths(project.Root), new CSharpScanner());
        workspace.Load();
        workspace.Generate();
        jobs = new JobQueue(events);
        var none = new FakeProjectLocator();
        var processes = new FakeProcessRunner();
        actions = new ViewerActions(
            workspace,
            new PolicyEditor(workspace),
            new CrapService(workspace, none, processes, new FakeCoverageReader(new(new Dictionary<string, IReadOnlyDictionary<int, int>>()))),
            new MutationService(workspace, none, processes, new FakeMutationReader()),
            jobs,
            companion,
            events);
    }

    public void Dispose() => project.Dispose();

    private Task<ActionResult> Do(string op, string? context = null, string? node = null, string? name = null, string? text = null, string? focus = null, JsonObject? selection = null) =>
        actions.HandleAsync(new ViewerAction(op, context, node, name, text, focus, selection), CancellationToken.None);

    private Policy SavedPolicy => JsonFile.Read<Policy>(workspace.Paths.PolicyFile)!;

    [Fact]
    public async Task UnknownOperationsFail() => Assert.False((await Do("explode")).Ok);

    [Fact]
    public async Task AMessageGoesToTheAgentWithItsContextAndRingsTheBell()
    {
        companion.Running = true;
        var selection = new JsonObject { ["id"] = "t:Shop.Domain.Order" };

        var result = await Do("message", context: "real", focus: "ns:Domain", text: "  Why?  ", selection: selection);

        Assert.True(result.Ok);
        var mail = Assert.Single(workspace.ToAgent.Take());
        Assert.Equal("message", mail.Op);
        Assert.Equal("Why?", (string?)mail.Data["text"]);
        Assert.Equal("real", (string?)mail.Data["context"]);
        Assert.Equal("Domain", (string?)mail.Data["focus"]!["namespace"]);
        Assert.Equal("t:Shop.Domain.Order", (string?)mail.Data["selection"]!["id"]);
        Assert.Equal(1, companion.Rings);
    }

    [Fact]
    public async Task AMessageIsQueuedWhenTheAgentIsNotRunning()
    {
        var result = await Do("message", text: "hello");
        Assert.Contains("not running", result.Message);
        Assert.Equal(0, companion.Rings);
        Assert.Equal(1, workspace.ToAgent.Count);
    }

    [Fact]
    public async Task AnEmptyMessageIsRefused()
    {
        Assert.False((await Do("message", text: " ")).Ok);
        Assert.Equal(0, workspace.ToAgent.Count);
    }

    [Fact]
    public async Task ChoosingAProposalTellsTheAgentWhichDiagramIsUnderDiscussion()
    {
        companion.Running = true;
        await Do("context", context: "p1");
        var mail = Assert.Single(workspace.ToAgent.Take());
        Assert.Equal("proposal", (string?)mail.Data["context"]);
        Assert.Equal("p1", (string?)mail.Data["proposalId"]);
        Assert.Equal("Split", (string?)mail.Data["name"]);
        Assert.Equal(1, companion.Rings);
    }

    [Fact]
    public async Task OmittingATypeOnTheRealDiagramEditsThePolicyAndNotifiesQuietly()
    {
        companion.Running = true;

        var result = await Do("omit", context: "real", node: "t:Shop.Web.Page");

        Assert.True(result.Ok);
        Assert.Equal(["Web.Page"], SavedPolicy.Omit);
        var mail = Assert.Single(workspace.ToAgent.Take());
        Assert.Equal("changed", mail.Op);
        Assert.Equal(0, companion.Rings);
        Assert.Contains("policy", events.Types);
    }

    [Fact]
    public async Task OmittingOnAProposalEditsOnlyThatProposal()
    {
        await Do("omit", context: "p1", node: "ns:Web");
        Assert.Equal(["Web"], SavedPolicy.Proposals.Single().Omit);
        Assert.Empty(SavedPolicy.Omit);
    }

    [Fact]
    public async Task OnlyNamespacesAndTypesCanBeOmitted()
    {
        Assert.False((await Do("omit", context: "p1", node: "g:core")).Ok);
        Assert.False((await Do("omit", context: "real", node: "ns:")).Ok);
        Assert.False((await Do("omit")).Ok);
    }

    [Fact]
    public async Task ProposalsCanBeCreatedRenamedAndDeleted()
    {
        var created = await Do("new-proposal", name: "Hexagonal");
        var id = (string?)created.Data!["proposalId"];
        Assert.Contains(SavedPolicy.Proposals, p => p.Id == id && p.Name == "Hexagonal");
        Assert.Equal("context", Assert.Single(workspace.ToAgent.Take()).Op);

        Assert.True((await Do("rename-proposal", context: id, name: "Ports")).Ok);
        Assert.Equal("Ports", SavedPolicy.Proposals.Single(p => p.Id == id).Name);

        Assert.True((await Do("delete-proposal", context: id)).Ok);
        Assert.DoesNotContain(SavedPolicy.Proposals, p => p.Id == id);
        Assert.Equal(["changed", "changed"], workspace.ToAgent.Take().Select(m => m.Op));
    }

    [Fact]
    public async Task RenameAndDeleteNeedTheirArguments()
    {
        Assert.False((await Do("rename-proposal", context: "p1")).Ok);
        Assert.False((await Do("delete-proposal")).Ok);
    }

    [Fact]
    public async Task RegenRescansInTheBackground()
    {
        project.Write("More.cs", "namespace Shop.Web { public class Menu { } }");

        Assert.True((await Do("regen")).Ok);
        var status = await Jobs.Finished(jobs, "Scan");

        Assert.Equal(JobState.Succeeded, status.State);
        Assert.Contains(workspace.Model.Types, t => t.Name == "Menu");
        Assert.Contains("model", events.Types);
    }

    [Fact]
    public async Task RefreshCrapReportsAFailedJob()
    {
        Assert.True((await Do("refresh-crap")).Ok);
        var status = await Jobs.Finished(jobs, "CRAP");
        Assert.Equal(JobState.Failed, status.State);
        Assert.Contains("No test projects", status.Message);
    }

    [Fact]
    public async Task MutatingANodeWithoutFilesFails() =>
        Assert.False((await Do("refresh-mutate", context: "p1", node: "g:core-missing")).Ok);

    [Fact]
    public async Task MutatingANodeQueuesAJobNamedForIt()
    {
        var result = await Do("refresh-mutate-all", context: "real", node: "ns:Domain");
        Assert.Contains("1 file", result.Message);
        var status = await Jobs.Finished(jobs, "Mutation (all): Domain");
        Assert.Equal(JobState.Succeeded, status.State);
    }

    [Fact]
    public async Task StartingTheAgentReportsWhyItCannot()
    {
        companion.Available = false;
        var result = await Do("start-agent");
        Assert.False(result.Ok);
        Assert.Contains("tmux", result.Message);
        Assert.Equal(0, companion.Starts);
    }

    [Fact]
    public async Task StartingTheAgentStartsItOnce()
    {
        Assert.True((await Do("start-agent")).Ok);
        Assert.Equal(1, companion.Starts);
        Assert.Contains("agent", events.Types);
    }

    [Fact]
    public async Task OpeningATerminalFallsBackToTheAttachCommand()
    {
        var failed = await Do("open-terminal");
        Assert.Contains("tmux attach -t milligram-test", failed.Message);
        companion.TerminalOpens = true;
        Assert.True((await Do("open-terminal")).Ok);
    }
}

public class JobQueueTests
{
    [Fact]
    public async Task AJobReportsItsLogAndResult()
    {
        var events = new FakeEvents();
        var jobs = new JobQueue(events);

        await jobs.Enqueue("Work", (log, _) =>
        {
            log("step one");
            return Task.FromResult("all done");
        });

        var status = jobs.Status;
        Assert.Equal(JobState.Succeeded, status.State);
        Assert.Equal("all done", status.Message);
        Assert.Contains("step one", status.Log);
        Assert.NotNull(status.FinishedAt);
        Assert.Contains("job", events.Types);
    }

    [Fact]
    public async Task AFailingJobKeepsTheError()
    {
        var jobs = new JobQueue(new FakeEvents());
        await jobs.Enqueue("Boom", (_, _) => throw new InvalidOperationException("kaput"));
        Assert.Equal(JobState.Failed, jobs.Status.State);
        Assert.Equal("kaput", jobs.Status.Message);
        Assert.Contains("error: kaput", jobs.Status.Log);
    }

    [Fact]
    public async Task JobsRunOneAtATimeInOrder()
    {
        var jobs = new JobQueue(new FakeEvents());
        var order = new List<string>();
        var release = new TaskCompletionSource();
        var first = jobs.Enqueue("First", async (_, _) =>
        {
            await release.Task;
            lock (order) order.Add("first");
            return "";
        });
        var second = jobs.Enqueue("Second", (_, _) =>
        {
            lock (order) order.Add("second");
            return Task.FromResult("");
        });
        await Jobs.Running(jobs, "First");

        Assert.Equal(["Second"], jobs.Status.Queued);
        Assert.Empty(order);
        release.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(["first", "second"], order);
        Assert.Equal("Second", jobs.Status.Name);
    }

    [Fact]
    public async Task JobsEnqueuedTogetherFromAPoolThreadRunInTheOrderTheyCame()
    {
        var jobs = new JobQueue(new FakeEvents());
        var order = new List<int>();

        // The web server and the file watcher enqueue from thread-pool threads, whose own work queue is last in, first out.
        var all = await Task.Run(() => Enumerable.Range(0, 20)
            .Select(i => jobs.Enqueue($"Job {i}", (_, _) =>
            {
                lock (order) order.Add(i);
                return Task.FromResult("");
            }))
            .ToList());
        await Task.WhenAll(all);

        Assert.Equal(Enumerable.Range(0, 20), order);
    }

    [Fact]
    public async Task TheLogKeepsOnlyTheNewestLines()
    {
        var jobs = new JobQueue(new FakeEvents());
        await jobs.Enqueue("Chatty", (log, _) =>
        {
            for (var i = 0; i < 1000; i++) log($"line {i}");
            return Task.FromResult("");
        });
        Assert.Equal(400, jobs.Status.Log.Count);
        Assert.Equal("line 999", jobs.Status.Log[^1]);
    }
}

public class AgentBriefingTests
{
    [Fact]
    public void TheBriefingIsWrittenIntoTheStateDirectory()
    {
        using var project = new TempProject();
        var paths = new ProjectPaths(project.Root);
        AgentBriefing.Write(paths);
        var text = File.ReadAllText(paths.BriefingFile);
        Assert.Contains("milligram mail", text);
        Assert.Contains("milligram tell display", text);
        Assert.Contains("Do not invent components", text);
    }
}

public class AgentLauncherTests : IDisposable
{
    private readonly TempProject project = new(("milligram.json", """{ "agent": { "command": "my-copilot" } }"""));
    private readonly Workspace workspace;
    private readonly FakeCompanion companion = new();

    public AgentLauncherTests()
    {
        workspace = new Workspace(new ProjectPaths(project.Root), new CSharpScanner());
        workspace.Load();
    }

    public void Dispose() => project.Dispose();

    private Task<AgentLauncher.Outcome> Start(bool wanted = true) => new AgentLauncher(workspace, companion).StartAsync(wanted, CancellationToken.None);

    private const string RunYourOwn = "  To run your own agent, start my-copilot in the project folder and tell it to read .milligram/agent.md.";

    [Fact]
    public async Task WithoutTheAgentTheBriefingIsWrittenAndTheBannerSaysHowToRunYourOwn()
    {
        var outcome = await Start(wanted: false);

        Assert.True(File.Exists(workspace.Paths.BriefingFile));
        Assert.Equal(new[] { "Agent: not started (--no-agent)", RunYourOwn }, outcome.Banner);
        Assert.False(outcome.Started);
        Assert.Equal(0, companion.Starts);
    }

    [Fact]
    public async Task WhenTheAgentCantStartTheBriefingIsWrittenAndTheBannerSaysWhy()
    {
        companion.Available = false;

        var outcome = await Start();

        Assert.True(File.Exists(workspace.Paths.BriefingFile));
        Assert.Equal(new[] { "Agent: not started (tmux is not installed.)", RunYourOwn }, outcome.Banner);
        Assert.False(outcome.Started);
        Assert.Equal(0, companion.Starts);
    }

    [Fact]
    public async Task AnAgentAlreadyRunningIsBriefedAndLeftToWhoeverStartedIt()
    {
        companion.Running = true;

        var outcome = await Start();

        Assert.True(File.Exists(workspace.Paths.BriefingFile));
        Assert.Equal(new[] { "Agent: already running — tmux attach -t milligram-test" }, outcome.Banner);
        Assert.False(outcome.Started);
        Assert.Equal(0, companion.Starts);
    }

    [Fact]
    public async Task AnAvailableAgentIsStartedAndStoppedLater()
    {
        var outcome = await Start();

        Assert.True(File.Exists(workspace.Paths.BriefingFile));
        Assert.Equal(new[] { "Agent: tmux attach -t milligram-test" }, outcome.Banner);
        Assert.True(outcome.Started);
        Assert.Equal(1, companion.Starts);
    }
}
