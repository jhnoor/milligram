using System.Net.WebSockets;

namespace Milligram.Adapters.Web;

/// <summary>Limits bytes sent but not yet parsed by one browser; socket writes alone cannot bound browser buffers.</summary>
public sealed class BrowserOutputWindow
{
    public const int Capacity = 128 * 1024;
    private readonly Lock gate = new();
    private TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int outstanding;

    public async Task ReserveAsync(int bytes, CancellationToken cancellation)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bytes, Capacity);
        while (true)
        {
            Task waiting;
            lock (gate)
            {
                cancellation.ThrowIfCancellationRequested();
                if (bytes <= Capacity - outstanding)
                {
                    outstanding += bytes;
                    return;
                }
                waiting = changed.Task;
            }
            await waiting.WaitAsync(cancellation);
        }
    }

    public void Acknowledge(int bytes)
    {
        TaskCompletionSource waiting;
        lock (gate)
        {
            if (bytes <= 0 || bytes > outstanding)
                throw new BrowserTerminalException(WebSocketCloseStatus.InvalidPayloadData, "Invalid terminal acknowledgement.");
            outstanding -= bytes;
            waiting = changed;
            changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        waiting.TrySetResult();
    }
}
