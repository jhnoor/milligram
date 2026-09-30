using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using Milligram.Adapters.Companion;
using Milligram.Application;

namespace Milligram.Tests.Adapters;

public class AgentPipeTests
{
    private static readonly HostHello Greeting = new(HostProtocol.Version, "0.2.0-test");

    [Fact]
    public async Task SeveralPipeClientsReceiveReplayLiveOutputAndTheFinalExitCode()
    {
        await using var host = new Host();
        host.Session.Publish("before"u8);
        await using var first = await host.Connect();
        await using var second = await host.Connect();
        Assert.Equal("before", Encoding.UTF8.GetString((await first.ReadAsync(host.Token))!.Payload));
        Assert.Equal("before", Encoding.UTF8.GetString((await second.ReadAsync(host.Token))!.Payload));
        await first.SendAsync(new HostFrame(HostFrameKind.Input, "typed"u8.ToArray()), host.Token);
        await Status(first, host.Token);
        Assert.Equal("typed", host.Terminal.Text);
        Assert.Equal(2, (await Status(second, host.Token)).Clients);
        host.Session.Publish(" after 🐱"u8);
        host.Session.Complete(17);
        host.Server.StopAccepting();
        Assert.Equal(" after 🐱", await OutputBeforeExit(first, 17, host.Token));
        Assert.Equal(" after 🐱", await OutputBeforeExit(second, 17, host.Token));
        await host.Running.WaitAsync(host.Token);
    }

    [Fact]
    public async Task ReconnectingToTheSameSessionReplaysItsLatestOutput()
    {
        await using var host = new Host();
        await using (var original = await host.Connect()) await Status(original, host.Token);
        host.Session.Publish("still running"u8);
        await using var next = await host.Connect();
        var frame = await next.ReadAsync(host.Token);
        Assert.Equal("still running", Encoding.UTF8.GetString(Assert.IsType<HostFrame>(frame).Payload));
        await next.SendAsync(HostProtocol.Resize(new TerminalSize(90, 30)), host.Token);
        Assert.Equal(90, (await Status(next, host.Token)).Columns);
        Assert.Equal(new TerminalSize(90, 30), host.Terminal.Sizes[^1]);
        Assert.False(host.Session.StopRequested.IsCompleted);
    }

    [Fact]
    public async Task ConcurrentClientWritesRemainWholeFrames()
    {
        await using var host = new Host();
        await using var client = await host.Connect();
        await Task.WhenAll(Enumerable.Range(0, 100).Select(index =>
            client.SendAsync(new HostFrame(HostFrameKind.Input, Encoding.UTF8.GetBytes($"{index},")), host.Token)));
        await Status(client, host.Token);
        Assert.Equal(Enumerable.Range(0, 100), host.Terminal.Text.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).Order());
    }

    [Fact]
    public async Task AClientThatStopsReadingCannotBlockTheListenerOrRetainItsSubscription()
    {
        await using var host = new Host();
        await using var slow = await host.Connect();
        await Status(slow, host.Token);
        host.Session.Publish(Enumerable.Repeat((byte)'x', HostProtocol.MaxPayload * 512).ToArray());
        await using var healthy = await host.Connect();
        Assert.Equal(1, (await Status(healthy, host.Token)).Clients);
        Assert.False(host.Session.StopRequested.IsCompleted);
        host.Session.Publish("still responsive"u8);
        Assert.Equal("still responsive", Encoding.UTF8.GetString((await healthy.ReadAsync(host.Token))!.Payload));
    }

    [Fact]
    public async Task RingCanFinishAfterTheSendingPipeCloses()
    {
        await using var host = new Host();
        await using (var client = await host.Connect())
            await client.SendAsync(new HostFrame(HostFrameKind.Ring, []), host.Token);
        await using var healthy = await host.Connect();
        await healthy.SendAsync(new HostFrame(HostFrameKind.Input, "after"u8.ToArray()), host.Token);
        await Status(healthy, host.Token);
        await host.Terminal.ReturnWritten.WaitAsync(host.Token);
        var text = host.Terminal.Text;
        Assert.Contains(AgentBriefing.Doorbell + "\r", text);
        Assert.DoesNotContain(AgentBriefing.Doorbell + "after\r", text);
        Assert.Contains("after", text);
    }

    [Fact]
    public async Task AProtocolMismatchExplainsWhichHostMustBeRestarted()
    {
        await using var host = new Host(new HostHello(2, "0.3.0"));
        var error = await Assert.ThrowsAsync<MilligramException>(() => host.Connect());
        Assert.Contains("protocol 2", error.Message);
        Assert.Contains("0.3.0", error.Message);
        Assert.Contains("restart", error.Message);
        Assert.False(host.Session.StopRequested.IsCompleted);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("wrong kind")]
    [InlineData("future")]
    public async Task InvalidGreetingsAreDisconnectedWithoutAffectingOtherClients(string input)
    {
        await using var host = new Host();
        await using var raw = await host.Raw();
        Assert.Equal(HostFrameKind.Hello, (await HostProtocol.ReadAsync(raw, host.Token))!.Kind);
        if (input == "missing") await raw.DisposeAsync();
        else
        {
            var frame = input switch
            {
                "malformed" => new HostFrame(HostFrameKind.Hello, "{}"u8.ToArray()),
                "wrong kind" => new HostFrame(HostFrameKind.Input, [13]),
                _ => HostProtocol.Json(HostFrameKind.Hello, Greeting with { Protocol = 2 }),
            };
            await HostProtocol.WriteAsync(raw, frame, host.Token);
            await Closed(raw, host.Token);
        }
        await using var healthy = await host.Connect();
        Assert.Equal(1, (await Status(healthy, host.Token)).Clients);
        Assert.Empty(host.Terminal.Text);
    }

    [Fact]
    public async Task AnIdleGreetingTimesOutAndDoesNotBlockAcceptingAnotherClient()
    {
        await using var host = new Host(timeout: TimeSpan.FromSeconds(1));
        await using var idle = await host.Raw();
        Assert.Equal(HostFrameKind.Hello, (await HostProtocol.ReadAsync(idle, host.Token))!.Kind);
        await using var healthy = await host.Connect();
        Assert.Equal(1, (await Status(healthy, host.Token)).Clients);
        await Closed(idle, host.Token);
    }

    [Theory]
    [InlineData(HostFrameKind.Hello)]
    [InlineData(HostFrameKind.Output)]
    [InlineData(HostFrameKind.Exited)]
    public async Task ServerOnlyCommandsCloseOnlyTheSendingClient(HostFrameKind kind)
    {
        await using var host = new Host();
        await using var bad = await host.Connect();
        var frame = kind switch
        {
            HostFrameKind.Hello => HostProtocol.Json(kind, Greeting),
            HostFrameKind.Exited => HostProtocol.Exited(0),
            _ => new HostFrame(kind, "unexpected"u8.ToArray()),
        };
        await bad.SendAsync(frame, host.Token);
        Assert.Null(await bad.ReadAsync(host.Token));
        await using var healthy = await host.Connect();
        Assert.Equal(1, (await Status(healthy, host.Token)).Clients);
        Assert.Empty(host.Terminal.Text);
    }

    [Fact]
    public async Task AStopFrameReachesTheOwningHost()
    {
        await using var host = new Host();
        await using var client = await host.Connect();
        await client.SendAsync(new HostFrame(HostFrameKind.Stop, []), host.Token);
        await host.Session.StopRequested.WaitAsync(host.Token);
    }

    [Fact]
    public async Task OversizedFrameLengthsCloseTheClientWithoutWaitingForTheirBodies()
    {
        await using var host = new Host();
        await using var raw = await host.Raw();
        await HostProtocol.WriteAsync(raw, HostProtocol.Json(HostFrameKind.Hello, Greeting), host.Token);
        await HostProtocol.ReadAsync(raw, host.Token);
        await raw.WriteAsync(new byte[] { 255, 255, 255, 127 }, host.Token);
        await raw.FlushAsync(host.Token);
        await Closed(raw, host.Token);
        await using var healthy = await host.Connect();
        Assert.Equal(1, (await Status(healthy, host.Token)).Clients);
    }

    [Fact]
    public async Task HostCancellationClosesIdleConnectionsAndTheListener()
    {
        await using var host = new Host();
        await using var client = await host.Connect();
        await Status(client, host.Token);
        host.Cancel();
        await host.Running.WaitAsync(TimeSpan.FromSeconds(5));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.Null(await client.ReadAsync(deadline.Token));
    }

    [Fact]
    public async Task ConnectingToAnAbsentHostCanBeCancelled()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AgentPipeClient.ConnectAsync("mg-absent-" + Guid.NewGuid().ToString("N"), Greeting, cancellation.Token));
    }

    [Fact]
    public async Task AListenerCanOnlyBeRunOnce()
    {
        await using var host = new Host();
        await host.Server.Ready.WaitAsync(host.Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Server.RunAsync(host.Token));
        await using var client = await host.Connect();
        Assert.Equal(1, (await Status(client, host.Token)).Clients);
    }

    [Fact]
    public async Task FailureToCreateTheListenerAlsoFailsItsReadinessSignal()
    {
        using var terminal = new Terminal();
        using var session = new AgentSession(terminal, new TerminalSize(80, 24));
        using var server = new AgentPipeServer(session, "", Greeting, _ => { });
        await Assert.ThrowsAnyAsync<ArgumentException>(() => server.RunAsync(CancellationToken.None));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => server.Ready);
    }

    [Fact]
    public async Task AHostThatClosesWithoutGreetingProducesAnActionableError()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var endpoint = "mg-test-" + Guid.NewGuid().ToString("N")[..16];
        await using var pipe = new NamedPipeServerStream(endpoint, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var accepting = pipe.WaitForConnectionAsync(deadline.Token);
        var connecting = AgentPipeClient.ConnectAsync(endpoint, Greeting, deadline.Token);
        await accepting;
        await HostProtocol.ReadAsync(pipe, deadline.Token);
        await pipe.DisposeAsync();
        var error = await Assert.ThrowsAsync<EndOfStreamException>(() => connecting);
        Assert.Contains("closed before greeting", error.Message);
    }

    [Fact]
    public async Task AFailedClientHandshakeClosesItsEndOfThePipe()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var endpoint = "mg-test-" + Guid.NewGuid().ToString("N")[..16];
        await using var pipe = new NamedPipeServerStream(endpoint, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var accepting = pipe.WaitForConnectionAsync(deadline.Token);
        var connecting = AgentPipeClient.ConnectAsync(endpoint, Greeting, deadline.Token);
        await accepting;
        await HostProtocol.ReadAsync(pipe, deadline.Token);
        await HostProtocol.WriteAsync(pipe, HostProtocol.Exited(0), deadline.Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => connecting);
        await Closed(pipe, deadline.Token);
    }

    [Fact]
    public async Task ConcurrentSendsDoNotEnterATransportWhileItsPreviousWriteIsPending()
    {
        using var stream = new DelayedStream();
        await using var client = new AgentPipeClient(stream);
        var first = client.SendAsync(new HostFrame(HostFrameKind.Input, "first"u8.ToArray()), CancellationToken.None);
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = client.SendAsync(new HostFrame(HostFrameKind.Input, "second"u8.ToArray()), CancellationToken.None);
        try { Assert.Equal(1, stream.Writes); }
        finally { stream.Release.TrySetResult(); }
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        stream.Position = 0;
        Assert.Equal("first", Encoding.UTF8.GetString((await HostProtocol.ReadAsync(stream, CancellationToken.None))!.Payload));
        Assert.Equal("second", Encoding.UTF8.GetString((await HostProtocol.ReadAsync(stream, CancellationToken.None))!.Payload));
    }

    private static async Task<AgentHostStatus> Status(AgentPipeClient client, CancellationToken cancellation)
    {
        await client.SendAsync(new HostFrame(HostFrameKind.Status, []), cancellation);
        while (await client.ReadAsync(cancellation) is { } frame)
            if (frame.Kind == HostFrameKind.Status) return HostProtocol.ReadJson<AgentHostStatus>(frame);
        throw new EndOfStreamException("No host status response.");
    }

    private static async Task<string> OutputBeforeExit(AgentPipeClient client, int code, CancellationToken cancellation)
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

    private static async Task Closed(Stream stream, CancellationToken cancellation)
    {
        try { Assert.Null(await HostProtocol.ReadAsync(stream, cancellation)); }
        catch (IOException) { }
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(10));
        private readonly string endpoint = "mg-test-" + Guid.NewGuid().ToString("N")[..16];
        public Terminal Terminal { get; } = new();
        public AgentSession Session { get; }
        public AgentPipeServer Server { get; }
        public Task Running { get; }
        public ConcurrentQueue<string> Logs { get; } = new();
        public CancellationToken Token => lifetime.Token;

        public Host(HostHello? greeting = null, TimeSpan? timeout = null)
        {
            Session = new AgentSession(Terminal, new TerminalSize(80, 24));
            Server = new AgentPipeServer(Session, endpoint, greeting ?? Greeting, Logs.Enqueue)
            {
                GreetingTimeout = timeout ?? TimeSpan.FromSeconds(3),
            };
            Running = Server.RunAsync(Token);
        }

        public async Task<AgentPipeClient> Connect()
        {
            await Server.Ready.WaitAsync(Token);
            return await AgentPipeClient.ConnectAsync(endpoint, Greeting, Token);
        }

        public async Task<NamedPipeClientStream> Raw()
        {
            await Server.Ready.WaitAsync(Token);
            var pipe = new NamedPipeClientStream(".", endpoint, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(Token);
            return pipe;
        }

        public void Cancel() => lifetime.Cancel();

        public async ValueTask DisposeAsync()
        {
            lifetime.Cancel();
            try { await Running.WaitAsync(TimeSpan.FromSeconds(5)); }
            finally { Server.Dispose(); Session.Dispose(); Terminal.Dispose(); lifetime.Dispose(); }
        }
    }

    private sealed class Terminal : IAgentTerminal
    {
        private readonly TerminalInput input = new();
        public string Text => Encoding.UTF8.GetString(input.ToArray());
        public Task ReturnWritten => input.ReturnWritten.Task;
        public int Pid => 123;
        public Stream Input => input;
        public Stream Output => Stream.Null;
        public Task<int> Exited => Task.FromResult(0);
        public List<TerminalSize> Sizes { get; } = [];
        public void Resize(TerminalSize size) => Sizes.Add(size);
        public void Stop() { }
        public void Dispose() => input.Dispose();
    }

    private sealed class TerminalInput : MemoryStream
    {
        public TaskCompletionSource ReturnWritten { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer.Span);
            if (buffer.Span.SequenceEqual("\r"u8)) ReturnWritten.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DelayedStream : MemoryStream
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Writes { get; private set; }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Writes++;
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            await base.WriteAsync(buffer, cancellationToken);
        }
    }
}
