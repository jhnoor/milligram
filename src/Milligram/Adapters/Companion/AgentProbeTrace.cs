using System.Diagnostics;

namespace Milligram.Adapters.Companion;

/// <summary>Records bounded timing labels only; project paths and terminal content never enter probe diagnostics.</summary>
internal sealed class AgentProbeTrace
{
    private readonly long started = Stopwatch.GetTimestamp();
    private readonly List<(string Stage, double Milliseconds)> stages = [];

    public void Mark(string stage)
    {
        if (stages.Count < 32) stages.Add((stage, Stopwatch.GetElapsedTime(started).TotalMilliseconds));
    }

    public override string ToString() => "Agent probe (elapsed ms): " + string.Join(", ",
        stages.Select(stage => FormattableString.Invariant($"{stage.Stage}={stage.Milliseconds:F1}")));
}
