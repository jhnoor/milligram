using System.Diagnostics;
using Milligram.Application;

namespace Milligram.Adapters.Files;

/// <summary>Keeps imports, generator inputs and linked files live without thousands of native watchers or output-triggered scan loops.</summary>
public sealed class ScanInputPoller : IDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly Task loop;

    public ScanInputPoller(Func<IScanInputs?> inputs, Action changed, TimeSpan minimum)
    {
        var token = stop.Token;
        loop = Task.Run(async () =>
        {
            IScanInputs? previous = null;
            string? version = null;
            while (!token.IsCancellationRequested)
            {
                var started = Stopwatch.GetTimestamp();
                if (inputs() is { } current)
                {
                    if (!ReferenceEquals(previous, current)) { previous = current; version = current.Version; }
                    if (current.ReadVersion() is { } observed && observed != version)
                    {
                        if (token.IsCancellationRequested) return;
                        version = observed;
                        changed();
                    }
                }
                var delay = TimeSpan.FromTicks(Math.Max(minimum.Ticks, Stopwatch.GetElapsedTime(started).Ticks * 10));
                try { await Task.Delay(delay, token); }
                catch (OperationCanceledException) { return; }
            }
        });
    }

    public void Dispose()
    {
        stop.Cancel();
        try { loop.Wait(TimeSpan.FromSeconds(5)); }
        catch (AggregateException) { }
        stop.Dispose();
    }
}
