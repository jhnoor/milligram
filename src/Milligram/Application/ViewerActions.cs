using System.Text.Json.Nodes;
using Milligram.Domain.Hierarchy;
using Milligram.Domain.Mail;
using Milligram.Domain.Policies;
using Milligram.Domain.Views;

namespace Milligram.Application;

public sealed record ViewerAction(
    string Op,
    string? Context = null,
    string? Node = null,
    string? Name = null,
    string? Text = null,
    string? Focus = null,
    JsonObject? Selection = null);

public sealed record ActionResult(bool Ok, string? Message = null, JsonObject? Data = null)
{
    public static ActionResult Done(string? message = null, JsonObject? data = null) => new(true, message, data);
    public static ActionResult Fail(string message) => new(false, message);
}

/// <summary>
/// What the viewer's buttons and menus do. Mechanical work (scans, metrics, policy edits) runs here;
/// conversation (context, questions) goes to the companion agent by mail.
/// </summary>
public sealed class ViewerActions(
    Workspace workspace,
    PolicyEditor editor,
    CrapService crap,
    MutationService mutation,
    JobQueue jobs,
    ICompanion companion,
    IViewerEvents events)
{
    public async Task<ActionResult> HandleAsync(ViewerAction action, CancellationToken cancellation)
    {
        try
        {
            return action.Op switch
            {
                "regen" => Regenerate(),
                "refresh-crap" => RefreshCrap(),
                "refresh-mutate" => RefreshMutation(action, all: false),
                "refresh-mutate-all" => RefreshMutation(action, all: true),
                "omit" => Omit(action),
                "new-proposal" => NewProposal(action),
                "rename-proposal" => RenameProposal(action),
                "delete-proposal" => DeleteProposal(action),
                MailMessage.Ops.Context => Context(action),
                MailMessage.Ops.Message => Message(action),
                "start-agent" => await StartAgentAsync(cancellation),
                "open-terminal" => companion.OpenTerminal()
                    ? ActionResult.Done()
                    : ActionResult.Fail($"Could not open a terminal. Run: {companion.AttachCommand}"),
                _ => ActionResult.Fail($"Unknown action '{action.Op}'."),
            };
        }
        catch (MilligramException e)
        {
            return ActionResult.Fail(e.Message);
        }
    }

    public void Regenerate(string reason = "Scan")
    {
        jobs.Enqueue(reason, (log, _) =>
        {
            var model = workspace.Generate();
            events.Publish("model");
            return Task.FromResult($"{model.Types.Count} types, {model.Edges.Count} dependencies.");
        });
    }

    private ActionResult Regenerate()
    {
        Regenerate("Scan");
        return ActionResult.Done("Regenerating the model.");
    }

    private ActionResult RefreshCrap()
    {
        jobs.Enqueue("CRAP", async (log, token) =>
        {
            var result = await crap.RunAsync(null, log, token);
            events.Publish("metrics");
            return $"{result.Members} members scored" + (result.TestExitCode != 0 ? " (some tests failed)" : "") + ".";
        });
        return ActionResult.Done("Running tests with coverage.");
    }

    private ActionResult RefreshMutation(ViewerAction action, bool all)
    {
        var files = action.Node is null ? [] : workspace.FilesOf(action.Context, action.Node);
        if (action.Node is not null && files.Count == 0) return ActionResult.Fail("Nothing to mutate there.");
        var label = action.Node is null ? "project" : Label(action);
        jobs.Enqueue(all ? $"Mutation (all): {label}" : $"Mutation: {label}", async (log, token) =>
        {
            var result = await mutation.RunAsync(files, all, log, token);
            events.Publish("metrics");
            return $"{result.Members} members, {result.Mutants} mutants.";
        });
        return ActionResult.Done($"Mutating {(files.Count == 0 ? "all files" : $"{files.Count} file(s)")}.");
    }

    private ActionResult Omit(ViewerAction action)
    {
        if (action.Node is null) return ActionResult.Fail("Omit needs a node.");
        var target = TargetOf(action.Context, action.Node);
        if (target is null) return ActionResult.Fail("Only namespaces and types can be omitted.");
        var proposal = ProposalId(action.Context);
        editor.Omit(target, proposal);
        Notify(new JsonObject { ["what"] = "omit", ["target"] = target, ["proposalId"] = proposal });
        events.Publish("policy");
        return ActionResult.Done($"Omitted {target}.");
    }

    private ActionResult NewProposal(ViewerAction action)
    {
        var proposal = editor.NewProposal(action.Name);
        events.Publish("policy");
        Mail(MailMessage.Ops.Context, ContextData(proposal.Id, action.Focus));
        return ActionResult.Done($"Created proposal {proposal.Name}.", new JsonObject { ["proposalId"] = proposal.Id });
    }

    private ActionResult RenameProposal(ViewerAction action)
    {
        if (action.Context is null || action.Name is null) return ActionResult.Fail("Rename needs a proposal and a name.");
        editor.RenameProposal(action.Context, action.Name);
        Notify(new JsonObject { ["what"] = "rename-proposal", ["proposalId"] = action.Context, ["name"] = action.Name });
        events.Publish("policy");
        return ActionResult.Done();
    }

    private ActionResult DeleteProposal(ViewerAction action)
    {
        if (action.Context is null) return ActionResult.Fail("Delete needs a proposal.");
        editor.DeleteProposal(action.Context);
        Notify(new JsonObject { ["what"] = "delete-proposal", ["proposalId"] = action.Context });
        events.Publish("policy");
        return ActionResult.Done();
    }

    private ActionResult Context(ViewerAction action)
    {
        Mail(MailMessage.Ops.Context, ContextData(action.Context, action.Focus));
        return ActionResult.Done();
    }

    private ActionResult Message(ViewerAction action)
    {
        if (string.IsNullOrWhiteSpace(action.Text)) return ActionResult.Fail("Say something first.");
        var data = ContextData(action.Context, action.Focus);
        data["text"] = action.Text.Trim();
        if (action.Selection is not null) data["selection"] = action.Selection.DeepClone();
        Mail(MailMessage.Ops.Message, data);
        return ActionResult.Done(companion.IsRunning() ? "Sent to the agent." : "Queued; the agent is not running. If you run your own, tell it to run `milligram mail`.");
    }

    private async Task<ActionResult> StartAgentAsync(CancellationToken cancellation)
    {
        if (!companion.IsAvailable(out var reason)) return ActionResult.Fail(reason);
        await companion.StartAsync(cancellation);
        events.Publish("agent");
        return ActionResult.Done($"Agent session {companion.SessionName} started.");
    }

    private void Mail(string op, JsonObject data)
    {
        workspace.ToAgent.Post(op, data);
        if (companion.IsRunning()) companion.Ring();
    }

    /// <summary>Tells the agent the viewer edited milligram.json, without waking it.</summary>
    private void Notify(JsonObject data) => workspace.ToAgent.Post(MailMessage.Ops.Changed, data);

    private JsonObject ContextData(string? contextId, string? focus)
    {
        var tree = workspace.Tree(contextId);
        var data = tree.IsProposal
            ? new JsonObject { ["context"] = "proposal", ["proposalId"] = tree.ContextId, ["name"] = tree.ContextName }
            : new JsonObject { ["context"] = DiagramTree.RealContext };
        if (focus is not null && tree.TryFind(focus) is { } node) data["focus"] = new JsonObject { ["id"] = node.Id, ["label"] = node.Label, ["namespace"] = node.Path };
        return data;
    }

    private string? ProposalId(string? contextId) =>
        contextId is not null && workspace.Policy.FindProposal(contextId) is not null ? contextId : null;

    private string? TargetOf(string? contextId, string nodeId)
    {
        var tree = workspace.Tree(contextId);
        if (nodeId.StartsWith(ViewBuilder.TypeIdPrefix, StringComparison.Ordinal) && tree.Type(nodeId[ViewBuilder.TypeIdPrefix.Length..]) is { } type)
            return NamePath.RelativeName(type, workspace.Policy.Prefix);
        return tree.TryFind(nodeId) is { Kind: NodeKind.Namespace, Path: { Length: > 0 } path } ? path : null;
    }

    private string Label(ViewerAction action) =>
        TargetOf(action.Context, action.Node!) ?? workspace.Tree(action.Context).TryFind(action.Node!)?.Label ?? action.Node!;
}
