using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Threading.Channels;
using Milligram.Adapters.Companion;

namespace Milligram.Tests.Adapters;

public class AgentHostRuntimeTests
{
    [Fact]
    public async Task NativeCleanupDoesNotWaitForAThreadPoolWorkerOrKeepTheProcessAlive()
    {
        await using var host = new Host();
        host.Terminal.Finish(17);
        Assert.Equal(0, await host.Running.WaitAsync(host.Token));
        Assert.False(host.Terminal.DisposalThreadPool);
        Assert.True(host.Terminal.DisposalBackground);
    }

    [Fact]
    public async Task ADelayedCleanupDispatchIsReportedAndStillRunsAfterTheDeadline()
    {
        var queued = new TaskCompletionSource<Action>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = new Host(timeout: TimeSpan.FromMilliseconds(100), dispatch: dispose =>
        {
            queued.SetResult(dispose);
            return completed.Task;
        });
        host.Terminal.Finish(17);
        var cleanup = await queued.Task.WaitAsync(host.Token);
        try
        {
            Assert.Equal(1, await host.Running.WaitAsync(host.Token));
            Assert.Equal(0, host.Terminal.Disposals);
            Assert.Contains("Terminal cleanup: Disposal did not start before the shutdown deadline.", host.Logs);
        }
        finally
        {
            cleanup();
            completed.SetResult();
        }
        await host.Runtime.TerminalCleanup!.WaitAsync(host.Token);
        Assert.Equal(1, host.Terminal.Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BlockedNativeCleanupIsBoundedAndCanFinishOrFailAfterShutdown(bool fail)
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = new Host(timeout: TimeSpan.FromMilliseconds(100));
        host.Terminal.BeforeDispose = () => { entered.TrySetResult(); release.Wait(); };
        if (fail) host.Terminal.DisposeError = new IOException("late native failure");
        host.Terminal.Finish(17);
        string[] logs = [];
        try
        {
            await entered.Task.WaitAsync(host.Token);
            Assert.Equal(1, await host.Running.WaitAsync(host.Token));
            Assert.Contains("Terminal cleanup: Disposal started but did not finish before the shutdown deadline.", host.Logs);
            Assert.False(host.Runtime.TerminalCleanup!.IsCompleted);
            logs = host.Logs.ToArray();
        }
        finally { release.Set(); }
        if (fail)
        {
            var error = await Assert.ThrowsAsync<IOException>(() => host.Runtime.TerminalCleanup!.WaitAsync(host.Token));
            Assert.Same(host.Terminal.DisposeError, error);
        }
        else await host.Runtime.TerminalCleanup!.WaitAsync(host.Token);
        Assert.Equal(1, host.Terminal.Disposals);
        Assert.True(host.Terminal.Disposed.Task.IsCompletedSuccessfully);
        Assert.Equal(logs, host.Logs);
    }

    [Fact]
    public async Task ANativeTimeoutErrorIsNotMistakenForTheCleanupDeadline()
    {
        await using var host = new Host();
        host.Terminal.DisposeError = new TimeoutException("native timeout");
        host.Terminal.Finish(17);
        Assert.Equal(1, await host.Running.WaitAsync(host.Token));
        Assert.Contains("Terminal cleanup: native timeout", host.Logs);
    }

    [Fact]
    public async Task FinalOutputDrainsBeforeTheNonzeroExitAndAllClientsClose()
    {
        await using var host = new Host();
        await using var first = await host.Connect();
        await using var second = await host.Connect();
        host.Terminal.FinishOnStop = false;
        host.Terminal.Exit.TrySetResult(17);
        await host.Terminal.Stopped.Task.WaitAsync(host.Token);
        Assert.False(host.Running.IsCompleted);
        host.Terminal.OutputStream.Append("last 🐱 response");
        host.Terminal.OutputStream.End();
        Assert.Equal("last 🐱 response", await Drain(first, 17, host.Token));
        Assert.Equal("last 🐱 response", await Drain(second, 17, host.Token));
        Assert.Equal(0, await host.Running.WaitAsync(host.Token));
        Assert.Equal(1, host.Terminal.Stops);
        Assert.Equal(1, host.Terminal.Disposals);
        Assert.Equal(["Agent exited (17)."], host.Logs);
    }

    [Fact]
    public async Task OutputEofCanPrecedeTheNativeExitEventWithoutKillingTheAgent()
    {
        await using var host = new Host();
        host.Terminal.OutputStream.End();
        await host.Terminal.OutputStream.ReachedEof.Task.WaitAsync(host.Token);
        Assert.Equal(0, host.Terminal.Stops);
        host.Terminal.Exit.TrySetResult(17);
        Assert.Equal(0, await host.Running.WaitAsync(host.Token));
        Assert.Equal(["Agent exited (17)."], host.Logs);
    }

    [Fact]
    public async Task AStopFrameEndsTheTreeAndDeliversItsExitToTheRequester()
    {
        await using var host = new Host();
        await using var client = await host.Connect();
        await client.SendAsync(new HostFrame(HostFrameKind.Stop, []), host.Token);
        Assert.Equal("", await Drain(client, 137, host.Token));
        Assert.Equal(0, await host.Running.WaitAsync(host.Token));
        Assert.Equal(1, host.Terminal.Stops);
        Assert.Equal(1, host.Terminal.Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationCleansUpEvenBeforeTheHostPublishesReadiness(bool alreadyCancelled)
    {
        await using var host = new Host(cancelled: alreadyCancelled);
        if (!alreadyCancelled)
        {
            await host.Ready.Task.WaitAsync(host.Token);
            host.Cancel();
        }
        Assert.Equal(0, await host.Running.WaitAsync(host.Token));
        Assert.Equal(!alreadyCancelled, host.Ready.Task.IsCompletedSuccessfully);
        Assert.Equal(1, host.Terminal.Stops);
        Assert.Equal(1, host.Terminal.Disposals);
    }

    [Fact]
    public async Task FailedDiscoveryPublicationStillStopsAndDisposesTheTerminal()
    {
        await using var host = new Host(ready: () => throw new IOException("discovery failed"));
        Assert.Equal(1, await host.Running.WaitAsync(host.Token));
        Assert.Contains("discovery failed", host.Logs);
        Assert.Equal(1, host.Terminal.Stops);
        Assert.Equal(1, host.Terminal.Disposals);
    }

    [Fact]
    public async Task ListenerStartupFailureCannotLeaveTheAgentAlive()
    {
        await using var host = new Host(endpoint: "");
        Assert.Equal(1, await host.Running.WaitAsync(host.Token));
        Assert.False(host.Ready.Task.IsCompleted);
        Assert.Equal(1, host.Terminal.Stops);
        Assert.Equal(1, host.Terminal.Disposals);
        Assert.True(host.Logs.Count >= 2);
    }

    [Fact]
    public async Task AListenerThatStopsUnexpectedlyShutsDownTheTerminal()
    {
        await using var host = new Host();
        await host.Ready.Task.WaitAsync(host.Token);
        host.Server.StopAccepting();
        Assert.Equal(1, await host.Running.WaitAsync(host.Token));
        Assert.Contains(host.Logs, line => line.Contains("listener stopped", StringComparison.Ordinal));
        Assert.Equal(1, host.Terminal.Disposals);
    }

    [Fact]
    public async Task AFailedOutputReadIsReportedAfterTheTerminalIsDisposed()
    {
        await using var host = new Host();
        host.Terminal.OutputStream.End(new IOException("PTY read failed"));
        Assert.Equal(1, await host.Running.WaitAsync(host.Token));
        Assert.Contains("PTY read failed", host.Logs);
        Assert.Equal(1, host.Terminal.Stops);
        Assert.Equal(1, host.Terminal.Disposals);
    }

    [Fact]
    public async Task ClosingOutputWithoutExitingCannotHoldTheHostForever()
    {
        await using var host = new Host(timeout: TimeSpan.FromMilliseconds(100));
        host.Terminal.OutputStream.End();
        Assert.Equal(1, await host.Running.WaitAsync(host.Token));
        Assert.Equal(1, host.Terminal.Stops);
        Assert.Equal(1, host.Terminal.Disposals);
        Assert.Contains("Agent exited (137).", host.Logs);
    }

    [Fact]
    public async Task AFailedTreeStopStillDrainsOutputAndDisposesNativeHandles()
    {
        await using var host = new Host();
        await using var client = await host.Connect();
        host.Terminal.StopError = new IOException("tree stop failed");
        host.Terminal.OutputStream.Append("last words");
        host.Terminal.Finish(17);
        Assert.Equal("last words", await Drain(client, 17, host.Token));
        Assert.Equal(1, await host.Running.WaitAsync(host.Token));
        Assert.Contains("Terminal stop: tree stop failed", host.Logs);
        Assert.Equal(1, host.Terminal.Disposals);
    }

    [Fact]
    public async Task AnUnreportedExitAndStuckOutputAreBoundedAndReleasedByDisposal()
    {
        await using var host = new Host(timeout: TimeSpan.FromMilliseconds(100));
        host.Terminal.FinishOnStop = false;
        host.Cancel();
        Assert.Equal(1, await host.Running.WaitAsync(host.Token));
        Assert.Contains(host.Logs, line => line.StartsWith("Terminal exit:", StringComparison.Ordinal));
        Assert.Contains(host.Logs, line => line.StartsWith("Terminal output:", StringComparison.Ordinal));
        Assert.Contains("Agent exited (-1).", host.Logs);
        Assert.DoesNotContain(host.Logs, line => line.StartsWith("Agent I/O cleanup:", StringComparison.Ordinal));
        await host.Runtime.TerminalCleanup!.WaitAsync(host.Token);
        Assert.Equal(1, host.Terminal.Disposals);
        Assert.True(host.Terminal.OutputStream.Cancelled);
    }

    [Fact]
    public async Task AReadThatIgnoresCancellationAndDisposalCannotKeepTheHostAlive()
    {
        await using var host = new Host(timeout: TimeSpan.FromMilliseconds(100));
        host.Terminal.FinishOnStop = false;
        host.Terminal.OutputStream.IgnoreCancellation = true;
        try
        {
            host.Cancel();
            Assert.Equal(1, await host.Running.WaitAsync(host.Token));
            await host.Runtime.TerminalCleanup!.WaitAsync(host.Token);
            Assert.Equal(1, host.Terminal.Disposals);
            Assert.Contains(host.Logs, line => line.StartsWith("Agent I/O cleanup:", StringComparison.Ordinal));
        }
        finally { host.Terminal.OutputStream.Release.TrySetResult(); }
    }

    [Fact]
    public async Task AReadReleasedByDisposalFinishesBeforeTheHostReturns()
    {
        await using var host = new Host(timeout: TimeSpan.FromMilliseconds(100));
        host.Terminal.FinishOnStop = false;
        host.Terminal.OutputStream.IgnoreCancellation = true;
        host.Cancel();
        try
        {
            await host.Terminal.Disposed.Task.WaitAsync(host.Token);
            Assert.False(host.Running.IsCompleted);
            host.Terminal.OutputStream.Release.TrySetResult();
            Assert.Equal(1, await host.Running.WaitAsync(host.Token));
            Assert.DoesNotContain(host.Logs, line => line.StartsWith("Agent I/O cleanup:", StringComparison.Ordinal));
        }
        finally { host.Terminal.OutputStream.Release.TrySetResult(); }
    }

    [Fact]
    public async Task AnIdleClientDuringGreetingCannotDelayShutdownIndefinitely()
    {
        await using var host = new Host(timeout: TimeSpan.FromMilliseconds(100));
        await host.Ready.Task.WaitAsync(host.Token);
        await using var idle = new NamedPipeClientStream(".", host.Endpoint, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await idle.ConnectAsync(host.Token);
        Assert.Equal(HostFrameKind.Hello, (await HostProtocol.ReadAsync(idle, host.Token))!.Kind);
        host.Terminal.Finish(17);
        Assert.Equal(0, await host.Running.WaitAsync(host.Token));
        Assert.Equal(1, host.Terminal.Disposals);
        try { Assert.Null(await HostProtocol.ReadAsync(idle, host.Token)); }
        catch (IOException) { }
    }

    [Fact]
    public async Task NativeDisposalErrorsAreReportedAfterClientsHaveReceivedExit()
    {
        await using var host = new Host();
        await using var client = await host.Connect();
        host.Terminal.DisposeError = new IOException("native close failed");
        host.Terminal.Finish(17);
        Assert.Equal("", await Drain(client, 17, host.Token));
        Assert.Equal(1, await host.Running.WaitAsync(host.Token));
        Assert.Contains("Terminal cleanup: native close failed", host.Logs);
    }

    [Fact]
    public async Task LoggingFailuresCannotPreventNativeCleanup()
    {
        await using var host = new Host(log: _ => throw new IOException("log failed"));
        host.Terminal.Finish(17);
        var error = await Assert.ThrowsAsync<IOException>(() => host.Running.WaitAsync(host.Token));
        Assert.Equal("log failed", error.Message);
        Assert.Equal(1, host.Terminal.Stops);
        Assert.Equal(1, host.Terminal.Disposals);
    }

    [Fact]
    public async Task TheRuntimeCannotStartAnotherOutputReaderOrListener()
    {
        await using var host = new Host();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.Runtime.RunAsync(() => { }, host.Token));
        Assert.Equal("The agent host is already running.", error.Message);
        Assert.Equal(0, host.Terminal.Stops);
        host.Terminal.Finish(17);
        Assert.Equal(0, await host.Running.WaitAsync(host.Token));
    }

    private static async Task<string> Drain(AgentPipeClient client, int code, CancellationToken cancellation)
    {
        using var output = new MemoryStream();
        var exited = false;
        while (await client.ReadAsync(cancellation) is { } frame)
        {
            Assert.False(exited);
            if (frame.Kind == HostFrameKind.Exited)
            {
                Assert.Equal(code, HostProtocol.ReadExitCode(frame));
                exited = true;
            }
            else
            {
                Assert.Equal(HostFrameKind.Output, frame.Kind);
                output.Write(frame.Payload);
            }
        }
        Assert.True(exited);
        return Encoding.UTF8.GetString(output.ToArray());
    }

    private sealed class Host : IAsyncDisposable
    {
        private static readonly HostHello Greeting = new(HostProtocol.Version, "0.2.0-test");
        private readonly CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        private readonly CancellationTokenSource lifetime = new();
        public string Endpoint { get; }
        public Terminal Terminal { get; } = new();
        public AgentSession Session { get; }
        public AgentPipeServer Server { get; }
        public AgentHostRuntime Runtime { get; }
        public Task<int> Running { get; }
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<string> Logs { get; } = new();
        public CancellationToken Token => deadline.Token;

        public Host(Action? ready = null, Action<string>? log = null, string? endpoint = null, bool cancelled = false, TimeSpan? timeout = null,
            Func<Action, Task>? dispatch = null)
        {
            Endpoint = endpoint ?? "mg-test-" + Guid.NewGuid().ToString("N")[..16];
            if (cancelled) lifetime.Cancel();
            Session = new AgentSession(Terminal, new TerminalSize(80, 24));
            Server = new AgentPipeServer(Session, Endpoint, Greeting, Logs.Enqueue);
            Runtime = new AgentHostRuntime(Terminal, Session, Server, log ?? Logs.Enqueue)
            {
                ShutdownTimeout = timeout ?? TimeSpan.FromSeconds(2),
                DispatchCleanup = dispatch ?? AgentHostRuntime.StartCleanup,
            };
            Running = Runtime.RunAsync(() => { ready?.Invoke(); Ready.TrySetResult(); }, lifetime.Token);
        }

        public void Cancel() => lifetime.Cancel();

        public async Task<AgentPipeClient> Connect()
        {
            await Ready.Task.WaitAsync(Token);
            var client = await AgentPipeClient.ConnectAsync(Endpoint, Greeting, Token);
            await client.SendAsync(new HostFrame(HostFrameKind.Status, []), Token);
            Assert.Equal(HostFrameKind.Status, (await client.ReadAsync(Token))!.Kind);
            return client;
        }

        public async ValueTask DisposeAsync()
        {
            lifetime.Cancel();
            Terminal.OutputStream.Release.TrySetResult();
            Terminal.Finish(-1);
            try { await Running.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (IOException) { }
            finally
            {
                try
                {
                    if (Runtime.TerminalCleanup is { } cleanup) await cleanup.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception error) when (ReferenceEquals(error, Terminal.DisposeError)) { }
                finally { Server.Dispose(); Session.Dispose(); lifetime.Dispose(); deadline.Dispose(); }
            }
        }
    }

    private sealed class Terminal : IAgentTerminal
    {
        public int Pid => 123;
        public Stream Input { get; } = new MemoryStream();
        public OutputStream OutputStream { get; } = new();
        public Stream Output => OutputStream;
        public TaskCompletionSource<int> Exit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<int> Exited => Exit.Task;
        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool FinishOnStop { get; set; } = true;
        public Exception? StopError { get; set; }
        public Exception? DisposeError { get; set; }
        public Action? BeforeDispose { get; set; }
        public int Stops { get; private set; }
        public int Disposals { get; private set; }
        public bool DisposalThreadPool { get; private set; }
        public bool DisposalBackground { get; private set; }
        public void Resize(TerminalSize size) { }
        public void Finish(int code) { Exit.TrySetResult(code); OutputStream.End(); }
        public void Stop()
        {
            Stops++;
            Stopped.TrySetResult();
            if (StopError is { } error) throw error;
            if (FinishOnStop) Finish(137);
        }
        public void Dispose()
        {
            DisposalThreadPool = Thread.CurrentThread.IsThreadPoolThread;
            DisposalBackground = Thread.CurrentThread.IsBackground;
            Disposals++;
            BeforeDispose?.Invoke();
            Finish(-1);
            Input.Dispose();
            OutputStream.Dispose();
            Disposed.TrySetResult();
            if (DisposeError is { } error) throw error;
        }
    }

    private sealed class OutputStream : Stream
    {
        private readonly Channel<byte[]> chunks = Channel.CreateUnbounded<byte[]>();
        public TaskCompletionSource ReachedEof { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IgnoreCancellation { get; set; }
        public bool Cancelled { get; private set; }
        public void Append(string text) => Assert.True(chunks.Writer.TryWrite(Encoding.UTF8.GetBytes(text)));
        public void End(Exception? error = null) => chunks.Writer.TryComplete(error);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try
            {
                if (await chunks.Reader.WaitToReadAsync(cancellationToken))
                {
                    var chunk = await chunks.Reader.ReadAsync(cancellationToken);
                    chunk.CopyTo(buffer);
                    return chunk.Length;
                }
                ReachedEof.TrySetResult();
                return 0;
            }
            catch (OperationCanceledException)
            {
                Cancelled = true;
                if (IgnoreCancellation) await Release.Task;
                throw;
            }
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
