using Milligram.Adapters.Companion;
using Milligram.Adapters.Files;
using Milligram.Adapters.Processes;
using Milligram.Adapters.Web;
using Milligram.Analysis.Coverage;
using Milligram.Analysis.CSharp;
using Milligram.Analysis.DotNet;
using Milligram.Analysis.Mutation;
using Milligram.Application;
using Milligram.Domain.Policies;

namespace Milligram.Main;

/// <summary>The composition root: the only place that knows every concrete class.</summary>
public sealed class Composition
{
    private readonly Func<CancellationToken, Task<AgentPipeClient>> connectTerminal;

    public Composition(string root, IReadOnlyList<string> selfCommand, string version,
        ScanSettings? scanSettings = null)
    {
        Paths = new ProjectPaths(root);
        Events = new EventHub();
        var processes = new ProcessRunner();
        var locator = new DotNetProjectLocator();
        var scanner = new ConfiguredScanner(new CSharpScanner(), locator,
            (files, configuration) => new ProjectScanner(processes.EvaluateProjectsAsync, files, configuration, () => processes.ScanInputs));
        Workspace = new Workspace(Paths, scanner, scanSettings);
        Jobs = new JobQueue(Events);
        Companion = new ConfiguredCompanion(() => Workspace.Policy.Agent.Host,
            new TmuxCompanion(Paths, () => Workspace.Policy, selfCommand),
            new AgentHostCompanion(Paths, () => Workspace.Policy, selfCommand, version,
                ProcessRunner.OnPath, ProcessRunner.StartDetached, ProcessRunner.TerminalIssue));
        Crap = new CrapService(Workspace, locator, processes, new CoberturaReader());
        Mutation = new MutationService(Workspace, locator, processes, new StrykerReportReader());
        Actions = new ViewerActions(Workspace, new PolicyEditor(Workspace), Crap, Mutation, Jobs, Companion, Events);
        Initializer = new ProjectInitializer(Paths, scanner, locator, new NewFilePublisher(), scanSettings);
        WatchLimits = new DrvFs();
        Doctor = new Doctor(Workspace, Crap, locator, processes, Companion, WatchLimits);
        Agent = new AgentLauncher(Workspace, Companion);
        AgentHost = new AgentHost(Paths, () => Workspace.Policy, selfCommand, version, ProcessRunner.StartTerminalAsync, ProcessRunner.DetachHostSession);
        var endpoint = new AgentHostFiles(Paths).Endpoint;
        connectTerminal = token => AgentPipeClient.ConnectAsync(endpoint,
            new HostHello(HostProtocol.Version, version), token);
        Attachment = new AgentAttachment(connectTerminal, LocalTerminal.Open);
    }

    public ProjectPaths Paths { get; }
    public EventHub Events { get; }
    public Workspace Workspace { get; }
    public JobQueue Jobs { get; }
    public ConfiguredCompanion Companion { get; }
    public CrapService Crap { get; }
    public MutationService Mutation { get; }
    public ViewerActions Actions { get; }
    public ProjectInitializer Initializer { get; }
    public IWatchLimits WatchLimits { get; }
    public Doctor Doctor { get; }
    public AgentLauncher Agent { get; }
    public AgentHost AgentHost { get; }
    public AgentAttachment Attachment { get; }

    public WebServer WebServer() => new(Workspace, Actions, Jobs, Companion, Events, () => Companion.Host == AgentHostKind.Milligram, connectTerminal);

    public InitializationInputs WatchInitialization() => new(Paths);
}
