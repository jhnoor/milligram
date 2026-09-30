using Milligram.Adapters.Companion;
using Porta.Pty;

namespace Milligram.Adapters.Processes;

/// <summary>Adapts one owned PTY and catches exits that happen before its event can be subscribed.</summary>
internal sealed class PtyTerminal : IAgentTerminal
{
    private readonly IPtyConnection connection;
    private readonly Action stopTree;
    private readonly TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int stopped;
    private int disposed;

    public PtyTerminal(IPtyConnection connection, Action stopTree)
    {
        this.connection = connection;
        this.stopTree = stopTree;
        connection.ProcessExited += OnExited;
        if (connection.WaitForExit(0)) exited.TrySetResult(connection.ExitCode);
    }

    public int Pid => connection.Pid;
    public Stream Input => connection.WriterStream;
    public Stream Output => connection.ReaderStream;
    public Task<int> Exited => exited.Task;
    public void Resize(TerminalSize size) => connection.Resize(size.Columns, size.Rows);

    public void Stop()
    {
        if (Interlocked.Exchange(ref stopped, 1) == 0) stopTree();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { Stop(); }
        finally
        {
            connection.ProcessExited -= OnExited;
            connection.Dispose();
        }
    }

    private void OnExited(object? sender, PtyExitedEventArgs e) => exited.TrySetResult(e.ExitCode);
}
