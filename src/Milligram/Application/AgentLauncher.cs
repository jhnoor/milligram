namespace Milligram.Application;

/// <summary>What starting the viewer does about the agent: brief it, then start it, find it running, or say why not.</summary>
public sealed class AgentLauncher(Workspace workspace, ICompanion companion)
{
    /// <summary>Automatic cleanup retains the original session's ownership across manual restarts.</summary>
    public sealed record Outcome(AgentOwnership? Ownership, IReadOnlyList<string> Banner)
    {
        public bool Started => Ownership is not null;
    }

    /// <summary>The briefing is written every time, so an agent the user runs in a terminal of their own can read it too.</summary>
    public async Task<Outcome> StartAsync(bool wanted, CancellationToken cancellation)
    {
        AgentBriefing.Write(workspace.Paths);
        if (!wanted) return NotStarted("--no-agent");
        if (workspace.PolicyError is { } error) return NotStarted(error);
        if (!companion.IsAvailable(out var reason)) return NotStarted(reason);
        if (companion.IsRunning()) return new(null, [$"Agent: already running — {companion.AttachCommand}"]);
        var started = await companion.StartAsync(cancellation);
        return new(started, [started is not null ? $"Agent: {companion.AttachCommand}" : $"Agent: already running — {companion.AttachCommand}"]);
    }

    private Outcome NotStarted(string reason) => new(null,
    [
        $"Agent: not started ({reason})",
        $"  To run your own agent, {AgentBriefing.RunYourOwn(workspace.Paths, workspace.Policy.Agent.Command)}.",
    ]);
}
