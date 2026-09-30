using Milligram.Application;
using Milligram.Domain.Policies;

namespace Milligram.Adapters.Companion;

/// <summary>Selects after policy loading, then keeps the backend so a reload cannot redirect session control.</summary>
public sealed class ConfiguredCompanion(Func<AgentHostKind> host, ICompanion tmux, ICompanion native) : ICompanion
{
    private readonly Lazy<(AgentHostKind Kind, ICompanion Companion)> selected = new(() => host() switch
    {
        AgentHostKind.Tmux => (AgentHostKind.Tmux, tmux),
        AgentHostKind.Milligram => (AgentHostKind.Milligram, native),
        _ => throw new MilligramException("Unsupported agent.host. Use \"tmux\" or \"milligram\" in milligram.json."),
    });

    public AgentHostKind Host => selected.Value.Kind;
    private ICompanion Companion => selected.Value.Companion;
    public string SessionName => Companion.SessionName;
    public string AttachCommand => Companion.AttachCommand;
    public bool IsAvailable(out string reason) => Companion.IsAvailable(out reason);
    public bool IsRunning() => Companion.IsRunning();
    public Task<bool> StartAsync(CancellationToken cancellation) => Companion.StartAsync(cancellation);
    public void Stop() => Companion.Stop();
    public void Ring() => Companion.Ring();
    public bool OpenTerminal() => Companion.OpenTerminal();
}
