namespace Milligram.Application;

/// <summary>What starting the viewer does about the agent: brief it, then start it, find it running, or say why not.</summary>
public sealed class AgentLauncher(Workspace workspace, ICompanion companion)
{
    /// <summary><see cref="Started"/> means this viewer started the agent, so it stops the agent again on exit.</summary>
    public sealed record Outcome(bool Started, IReadOnlyList<string> Banner);

    /// <summary>The briefing is written every time, so an agent the user runs in a terminal of their own can read it too.</summary>
    public async Task<Outcome> StartAsync(bool wanted, CancellationToken cancellation)
    {
        AgentBriefing.Write(workspace.Paths);
        if (!wanted) return NotStarted("--no-agent");
        if (!companion.IsAvailable(out var reason)) return NotStarted(reason);
        if (companion.IsRunning()) return new(false, [$"Agent: already running — {companion.AttachCommand}"]);
        await companion.StartAsync(cancellation);
        return new(true, [$"Agent: {companion.AttachCommand}"]);
    }

    private Outcome NotStarted(string reason) => new(false,
    [
        $"Agent: not started ({reason})",
        $"  To run your own agent, {AgentBriefing.RunYourOwn(workspace.Paths, workspace.Policy.Agent.Command)}.",
    ]);
}
