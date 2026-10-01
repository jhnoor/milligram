namespace Milligram.Adapters.Companion;

/// <summary>Stops the owned tree, drains output and exit to clients, and bounds the wait for native cleanup.</summary>
public sealed class AgentHostRuntime(IAgentTerminal terminal, AgentSession session, AgentPipeServer pipe, Action<string> log)
{
    private int running;
    internal TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(3);
    internal Func<Action, Task> DispatchCleanup { get; init; } = StartCleanup;
    internal Task? TerminalCleanup { get; private set; }

    /// <summary>Native disposal can block; it must not wait for or occupy the pool serving the output and pipe tasks.</summary>
    internal static Task StartCleanup(Action dispose) =>
        Task.Factory.StartNew(dispose, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    public async Task<int> RunAsync(Action ready, CancellationToken cancellation)
    {
        if (Interlocked.Exchange(ref running, 1) != 0) throw new InvalidOperationException("The agent host is already running.");
        using var operations = new CancellationTokenSource();
        var output = session.CopyOutputAsync(operations.Token);
        var listening = pipe.RunAsync(operations.Token);
        var errors = new List<string>();
        var exitCode = -1;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            await pipe.Ready.WaitAsync(cancellation);
            ready();
            var completed = await Task.WhenAny(terminal.Exited, session.StopRequested, output, listening).WaitAsync(cancellation);
            if (completed == listening)
            {
                await listening;
                throw new IOException("The agent listener stopped while the terminal was running.");
            }
            if (completed == output)
            {
                await output;
                // EOF can precede the native exit event; let the process report its actual exit code.
                await terminal.Exited.WaitAsync(ShutdownTimeout, cancellation);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) { errors.Add(error.Message); }

        Attempt("Terminal stop", terminal.Stop);
        await AttemptAsync("Terminal exit", async () => exitCode = await terminal.Exited.WaitAsync(ShutdownTimeout));
        await AttemptAsync("Terminal output", async () => await output.WaitAsync(ShutdownTimeout));
        Attempt("Session completion", () => session.Complete(exitCode));
        Attempt("Agent listener", pipe.StopAccepting);
        try { await listening.WaitAsync(ShutdownTimeout); }
        catch (TimeoutException) { }
        catch (Exception error) { errors.Add("Agent clients: " + error.Message); }

        Attempt("Agent cancellation", operations.Cancel);
        await AttemptAsync("Terminal cleanup", async () =>
        {
            var started = 0;
            TerminalCleanup = DispatchCleanup(() =>
            {
                Volatile.Write(ref started, 1);
                terminal.Dispose();
            });
            // A timed-out native close can fail later, after the host log and lease have closed.
            _ = TerminalCleanup.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            try { await TerminalCleanup.WaitAsync(ShutdownTimeout); }
            catch (TimeoutException) when (!TerminalCleanup.IsCompleted)
            {
                throw new TimeoutException(Volatile.Read(ref started) == 0
                    ? "Disposal did not start before the shutdown deadline."
                    : "Disposal started but did not finish before the shutdown deadline.");
            }
        });
        foreach (var task in new[] { output, listening })
        {
            try { await task.WaitAsync(ShutdownTimeout); }
            catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException) { }
            catch (Exception error) { errors.Add("Agent I/O cleanup: " + error.Message); }
        }
        log($"Agent exited ({exitCode}).");
        foreach (var error in errors) log(error);
        return errors.Count == 0 ? 0 : 1;

        void Attempt(string stage, Action action)
        {
            try { action(); }
            catch (Exception error) { errors.Add(stage + ": " + error.Message); }
        }

        async Task AttemptAsync(string stage, Func<Task> action)
        {
            try { await action(); }
            catch (Exception error) { errors.Add(stage + ": " + error.Message); }
        }
    }
}
