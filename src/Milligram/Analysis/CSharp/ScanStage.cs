namespace Milligram.Analysis.CSharp;

/// <summary>
/// One counted stage of a scan. Reports at most every <see cref="Step"/> items, and always the last, so a long
/// scan shows movement without flooding the console or the viewer's job panel. Thread-safe, because parsing
/// runs in parallel; reporting under the lock keeps the counts in order.
/// </summary>
internal sealed class ScanStage(Action<string> report, string verb, int total, string noun)
{
    private const int Step = 250;

    private readonly Lock gate = new();
    private int done;
    private int reported;

    public void Tick()
    {
        lock (gate)
        {
            done++;
            if (done < total && done - reported < Step) return;
            reported = done;
            report($"{verb} {done}/{total} {noun}.");
        }
    }
}
