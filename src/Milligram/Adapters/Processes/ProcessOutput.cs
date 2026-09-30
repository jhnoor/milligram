namespace Milligram.Adapters.Processes;

/// <summary>Owns redirected readers so a descendant cannot keep a finished command's output open forever.</summary>
internal sealed class ProcessOutput : IAsyncDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly TextReader output;
    private readonly TextReader error;
    public Task Completion { get; }

    public ProcessOutput(TextReader output, TextReader error, Action<string> onOutput, Action<string> onError, bool lines)
    {
        this.output = output;
        this.error = error;
        Completion = Task.WhenAll(Read(output, onOutput, lines), Read(error, onError, lines));
    }

    public async Task<bool> DrainAsync(TimeSpan timeout, CancellationToken cancellation)
    {
        try { await Completion.WaitAsync(timeout, cancellation).ConfigureAwait(false); return true; }
        catch (TimeoutException) when (!Completion.IsFaulted) { return false; }
    }

    /// <summary>Buffered stdout callbacks must not hold up starting the stderr reader.</summary>
    private async Task Read(TextReader reader, Action<string> receive, bool lines)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            if (lines)
            {
                while (await reader.ReadLineAsync(stop.Token).ConfigureAwait(false) is { } line) receive(line);
            }
            else
            {
                var buffer = new char[4096];
                int count;
                while ((count = await reader.ReadAsync(buffer.AsMemory(), stop.Token).ConfigureAwait(false)) > 0)
                    receive(new string(buffer, 0, count));
            }
        }
        catch (Exception exception) when (stop.IsCancellationRequested && exception is OperationCanceledException or IOException) { }
        catch
        {
            await stop.CancelAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync().ConfigureAwait(false);
        await Completion.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        try { output.Dispose(); }
        finally { error.Dispose(); stop.Dispose(); }
    }
}
