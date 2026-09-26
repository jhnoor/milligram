using Milligram.Adapters.Companion;
using Milligram.Adapters.Processes;
using Milligram.Adapters.Web;
using Milligram.Analysis.Coverage;
using Milligram.Analysis.CSharp;
using Milligram.Analysis.DotNet;
using Milligram.Analysis.Mutation;
using Milligram.Application;

namespace Milligram.Main;

/// <summary>The composition root: the only place that knows every concrete class.</summary>
public sealed class Composition
{
    public Composition(string root, string selfCommand)
    {
        Paths = new ProjectPaths(root);
        Events = new EventHub();
        var scanner = new CSharpScanner();
        var locator = new DotNetProjectLocator();
        var processes = new ProcessRunner();
        Workspace = new Workspace(Paths, scanner);
        Jobs = new JobQueue(Events);
        Companion = new TmuxCompanion(Paths, () => Workspace.Policy, selfCommand);
        Crap = new CrapService(Workspace, locator, processes, new CoberturaReader());
        Mutation = new MutationService(Workspace, locator, processes, new StrykerReportReader());
        Actions = new ViewerActions(Workspace, new PolicyEditor(Workspace), Crap, Mutation, Jobs, Companion, Events);
        Initializer = new ProjectInitializer(Paths, scanner, locator);
        Doctor = new Doctor(Workspace, Crap, locator, processes, Companion);
        Agent = new AgentLauncher(Workspace, Companion);
    }

    public ProjectPaths Paths { get; }
    public EventHub Events { get; }
    public Workspace Workspace { get; }
    public JobQueue Jobs { get; }
    public ICompanion Companion { get; }
    public CrapService Crap { get; }
    public MutationService Mutation { get; }
    public ViewerActions Actions { get; }
    public ProjectInitializer Initializer { get; }
    public Doctor Doctor { get; }
    public AgentLauncher Agent { get; }

    public WebServer WebServer() => new(Workspace, Actions, Jobs, Companion, Events);
}
