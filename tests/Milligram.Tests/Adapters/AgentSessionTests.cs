using System.IO.Pipelines;
using System.Text;
using Milligram.Adapters.Companion;
using Milligram.Application;

namespace Milligram.Tests.Adapters;

public class AgentSessionTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static readonly TerminalSize Initial = new(80, 24);

    [Fact]
    public async Task SeveralClientsReceiveReplayThenEveryLiveByteInOrder()
    {
        using var terminal = new FakeTerminal();
        using var session = new AgentSession(terminal, Initial);
        session.Publish("past"u8);
        using var first = session.Connect();
        session.Publish("middle"u8);
        using var second = session.Connect();
        session.Publish("tail"u8);
        session.Complete(17);

        Assert.Equal("pastmiddletail", await OutputBeforeExit(first, 17));
        Assert.Equal("pastmiddletail", await OutputBeforeExit(second, 17));
        Assert.False(first.Disconnected.IsCancellationRequested);
        using var later = session.Connect();
        Assert.Equal("pastmiddletail", await OutputBeforeExit(later, 17));
    }

    [Fact]
    public async Task ASlowClientIsDisconnectedWithoutLosingTheFastClientsOutput()
    {
        using var terminal = new FakeTerminal();
        using var session = Session(terminal, capacity: 2);
        using var slow = session.Connect();
        using var fast = session.Connect();
        for (var index = 0; index < 10; index++)
        {
            var bytes = Encoding.UTF8.GetBytes(index.ToString());
            session.Publish(bytes);
            Assert.Equal(bytes, (await Next(fast)).Payload);
        }
        Assert.True(slow.Disconnected.IsCancellationRequested);
        await Assert.ThrowsAsync<IOException>(() => slow.SendAsync(new HostFrame(HostFrameKind.Input, "bad"u8.ToArray())));
        await fast.SendAsync(new HostFrame(HostFrameKind.Status, []));
        Assert.Equal(1, HostProtocol.ReadJson<AgentHostStatus>(await Next(fast)).Clients);
        Assert.Empty(terminal.Written);
        Assert.False(session.StopRequested.IsCompleted);
    }

    [Fact]
    public async Task OverflowCannotWaitForAClientsCancellationCallback()
    {
        using var terminal = new FakeTerminal();
        using var session = Session(terminal, capacity: 1);
        using var client = session.Connect();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = client.Disconnected.Register(() => { entered.TrySetResult(); release.Task.GetAwaiter().GetResult(); });
        try
        {
            session.Publish("one"u8);
            await Task.Run(() => session.Publish("two"u8)).WaitAsync(Deadline);
            await entered.Task.WaitAsync(Deadline);
            Assert.True(client.Disconnected.IsCancellationRequested);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task NewClientsWaitPastAnUnreplayableControlString()
    {
        using var terminal = new FakeTerminal();
        using var session = Session(terminal, replayCapacity: 4);
        using var original = session.Connect();
        session.Publish("\u001b]long title"u8);
        using var joined = session.Connect();
        Assert.False(joined.Frames.TryRead(out _));
        session.Publish("more"u8);
        Assert.False(joined.Frames.TryRead(out _));
        session.Publish("\u001b\\OK"u8);
        Assert.Equal("OK"u8.ToArray(), (await Next(joined)).Payload);
        session.Publish("next"u8);
        Assert.Equal("next"u8.ToArray(), (await Next(joined)).Payload);
        session.Complete(0);
        Assert.Equal("\u001b]long titlemore\u001b\\OKnext", await OutputBeforeExit(original, 0));
    }

    [Fact]
    public async Task AWaitingClientCanResumeAfterABoundaryAtTheEndOfAChunk()
    {
        using var terminal = new FakeTerminal();
        using var session = Session(terminal, replayCapacity: 1);
        var character = Encoding.UTF8.GetBytes("🐱");
        session.Publish(character.AsSpan(0, 2));
        using var client = session.Connect();
        session.Publish(character.AsSpan(2));
        Assert.False(client.Frames.TryRead(out _));
        session.Publish("new"u8);
        Assert.Equal("new"u8.ToArray(), (await Next(client)).Payload);
    }

    [Fact]
    public async Task PartialReplayContinuesThroughTheRestOfItsCharacter()
    {
        using var terminal = new FakeTerminal();
        using var session = new AgentSession(terminal, Initial);
        var character = Encoding.UTF8.GetBytes("🐱");
        session.Publish(character.AsSpan(0, 2));
        using var client = session.Connect();
        session.Publish(character.AsSpan(2));
        session.Complete(0);
        Assert.Equal("🐱", await OutputBeforeExit(client, 0));
    }

    [Fact]
    public async Task SafeLiveOutputCanItselfEndInsideTheNextEscapeSequence()
    {
        using var terminal = new FakeTerminal();
        using var session = Session(terminal, replayCapacity: 1);
        session.Publish("\u001b]long title"u8);
        using var client = session.Connect();
        session.Publish("\a\u001b["u8);
        session.Publish("31mtext"u8);
        session.Complete(0);
        Assert.Equal("\u001b[31mtext", await OutputBeforeExit(client, 0));
    }

    [Fact]
    public async Task ReplayLargerThanACustomClientQueueDisconnectsThatClient()
    {
        using var terminal = new FakeTerminal();
        using var session = Session(terminal, capacity: 1);
        session.Publish(new byte[HostProtocol.MaxPayload * 3]);
        using var client = session.Connect();
        Assert.True(client.Disconnected.IsCancellationRequested);
        Assert.True(client.Frames.TryRead(out _));
        await Assert.ThrowsAsync<IOException>(() => client.Frames.Completion.WaitAsync(Deadline));
    }

    [Fact]
    public async Task ReplayAndLiveFramesStayWithinTheWireLimitAndOwnTheirBytes()
    {
        using var terminal = new FakeTerminal();
        using var session = new AgentSession(terminal, Initial);
        using var live = session.Connect();
        var bytes = Enumerable.Repeat((byte)'x', HostProtocol.MaxPayload * 2 + 7).ToArray();
        session.Publish(bytes);
        Array.Fill(bytes, (byte)'y');
        using var replay = session.Connect();
        session.Complete(0);
        var expected = new string('x', bytes.Length);
        Assert.Equal(expected, await OutputBeforeExit(live, 0));
        Assert.Equal(expected, await OutputBeforeExit(replay, 0));
    }

    [Fact]
    public async Task TheDefaultQueueFitsTheEntireReplayAndAnExitFrame()
    {
        using var terminal = new FakeTerminal();
        using var session = new AgentSession(terminal, Initial);
        session.Publish(Enumerable.Repeat((byte)'x', TerminalReplay.DefaultCapacity + 100).ToArray());
        using var client = session.Connect();
        session.Complete(0);
        Assert.False(client.Disconnected.IsCancellationRequested);
        Assert.Equal(new string('x', TerminalReplay.DefaultCapacity), await OutputBeforeExit(client, 0));
    }

    [Fact]
    public async Task ConcurrentSubscriptionCannotMissOrDuplicateBytes()
    {
        using var terminal = new FakeTerminal();
        using var session = new AgentSession(terminal, Initial);
        using var original = session.Connect();
        var joined = new System.Collections.Concurrent.ConcurrentBag<AgentSession.Client>();
        try
        {
            await Task.WhenAll(Enumerable.Range(0, 40).Select(index => Task.Run(() =>
            {
                session.Publish(Encoding.UTF8.GetBytes(index.ToString() + ","));
                joined.Add(session.Connect());
            })));
            session.Complete(0);
            var expected = await OutputBeforeExit(original, 0);
            foreach (var client in joined) Assert.Equal(expected, await OutputBeforeExit(client, 0));
        }
        finally { foreach (var client in joined) client.Dispose(); }
    }

    [Fact]
    public async Task TheOutputPumpPreservesSplitCharactersAndDrainsBeforeExit()
    {
        using var terminal = new FakeTerminal();
        using var session = new AgentSession(terminal, Initial);
        using var client = session.Connect();
        var pumping = session.CopyOutputAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.CopyOutputAsync(CancellationToken.None));
        var bytes = Encoding.UTF8.GetBytes("Grüße 🐱\u001b[31m");
        foreach (var value in bytes) await terminal.Produce([value]);
        await terminal.EndOutput();
        await pumping.WaitAsync(Deadline);
        session.Complete(23);
        Assert.Equal(Encoding.UTF8.GetString(bytes), await OutputBeforeExit(client, 23));
    }

    [Fact]
    public async Task CompletingTheSessionCancelsAnIdleOutputRead()
    {
        using var terminal = new FakeTerminal();
        using var session = new AgentSession(terminal, Initial);
        var pumping = session.CopyOutputAsync(CancellationToken.None);
        session.Complete(0);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pumping.WaitAsync(Deadline));
        Assert.False(terminal.Stopped);
    }

    [Fact]
    public async Task TheMostRecentlyActiveClientOwnsTheSizeAndFirstResizeRequestsARedraw()
    {
        using var terminal = new FakeTerminal();
        using var session = new AgentSession(terminal, Initial);
        using var first = session.Connect();
        using var second = session.Connect();
        await first.SendAsync(HostProtocol.Resize(Initial));
        var large = new TerminalSize(1000, 40);
        await second.SendAsync(HostProtocol.Resize(large));
        await first.SendAsync(new HostFrame(HostFrameKind.Input, "x"u8.ToArray()));
        Assert.Equal(Initial, terminal.Sizes[^1]);
        Assert.Equal(5, terminal.Sizes.Count);
        await first.SendAsync(HostProtocol.Resize(Initial));
        Assert.Equal(new[] { new TerminalSize(81, 24), Initial, new TerminalSize(999, 40), large, Initial }, terminal.Sizes);
        Assert.Equal("x", Encoding.UTF8.GetString(Assert.Single(terminal.Written)));
        await first.SendAsync(new HostFrame(HostFrameKind.Status, []));
        Assert.Equal(new AgentHostStatus(123, 2, 80, 24), HostProtocol.ReadJson<AgentHostStatus>(await Next(first)));
    }

    [Fact]
    public async Task InputFromAnotherClientCannotSplitTheDoorbellFromItsReturn()
    {
        using var terminal = new FakeTerminal();
        var delayed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var submit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var session = Session(terminal, delay: async token => { delayed.TrySetResult(); await submit.Task.WaitAsync(token); });
        using var bell = session.Connect();
        using var typing = session.Connect();
        var ringing = bell.SendAsync(new HostFrame(HostFrameKind.Ring, []));
        await delayed.Task.WaitAsync(Deadline);
        var input = typing.SendAsync(new HostFrame(HostFrameKind.Input, "typed"u8.ToArray()));
        Assert.False(input.IsCompleted);
        bell.Dispose();
        submit.TrySetResult();
        await Task.WhenAll(ringing, input).WaitAsync(Deadline);
        Assert.Equal(new[] { AgentBriefing.Doorbell, "\r", "typed" }, terminal.Written.Select(Encoding.UTF8.GetString));
    }

    [Fact]
    public async Task CancelledInputReleasesTheGateAndDoesNotWrite()
    {
        using var terminal = new FakeTerminal();
        using var session = new AgentSession(terminal, Initial);
        using var client = session.Connect();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendAsync(new HostFrame(HostFrameKind.Input, [3]), cancellation.Token));
        await client.SendAsync(new HostFrame(HostFrameKind.Input, [13]));
        Assert.Equal(new byte[] { 13 }, Assert.Single(terminal.Written));
    }

    [Fact]
    public async Task CancellingAQueuedWriterDoesNotStealTheDoorbellsInputGate()
    {
        using var terminal = new FakeTerminal();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var submit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var session = Session(terminal, delay: async token => { entered.TrySetResult(); await submit.Task.WaitAsync(token); });
        using var bell = session.Connect();
        using var queued = session.Connect();
        var ringing = bell.SendAsync(new HostFrame(HostFrameKind.Ring, []));
        await entered.Task.WaitAsync(Deadline);
        using var cancellation = new CancellationTokenSource();
        var typing = queued.SendAsync(new HostFrame(HostFrameKind.Input, "cancelled"u8.ToArray()), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => typing.WaitAsync(Deadline));
        var next = queued.SendAsync(new HostFrame(HostFrameKind.Input, "next"u8.ToArray()));
        Assert.False(next.IsCompleted);
        submit.TrySetResult();
        await Task.WhenAll(ringing, next).WaitAsync(Deadline);
        Assert.Equal(new[] { AgentBriefing.Doorbell, "\r", "next" }, terminal.Written.Select(Encoding.UTF8.GetString));
    }

    [Fact]
    public async Task TerminalWriteFailuresReleaseTheInputGate()
    {
        using var terminal = new FakeTerminal();
        using var session = new AgentSession(terminal, Initial);
        using var client = session.Connect();
        terminal.WriteError = new IOException("terminal closed");
        var frame = new HostFrame(HostFrameKind.Input, "test"u8.ToArray());
        await Assert.ThrowsAsync<IOException>(() => client.SendAsync(frame));
        terminal.WriteError = null;
        await client.SendAsync(frame).WaitAsync(Deadline);
        Assert.Equal(frame.Payload, Assert.Single(terminal.Written));
    }

    [Fact]
    public async Task BufferedTerminalInputIsFlushedForTypingAndBothDoorbellSteps()
    {
        using var terminal = new FakeTerminal(buffered: true);
        using var session = Session(terminal, delay: _ =>
        {
            Assert.Equal(new[] { "typed", AgentBriefing.Doorbell }, terminal.Written.Select(Encoding.UTF8.GetString));
            return Task.CompletedTask;
        });
        using var client = session.Connect();
        await client.SendAsync(new HostFrame(HostFrameKind.Input, "typed"u8.ToArray()));
        Assert.Equal("typed", Encoding.UTF8.GetString(Assert.Single(terminal.Written)));
        await client.SendAsync(new HostFrame(HostFrameKind.Ring, []));
        Assert.Equal(new[] { "typed", AgentBriefing.Doorbell, "\r" }, terminal.Written.Select(Encoding.UTF8.GetString));
    }

    [Theory]
    [InlineData(HostFrameKind.Stop)]
    [InlineData(HostFrameKind.Status)]
    public async Task CancelledControlRequestsHaveNoSideEffects(HostFrameKind kind)
    {
        using var terminal = new FakeTerminal();
        using var session = new AgentSession(terminal, Initial);
        using var client = session.Connect();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendAsync(new HostFrame(kind, []), cancellation.Token));
        Assert.False(session.StopRequested.IsCompleted);
        Assert.False(client.Frames.TryRead(out _));
    }

    [Fact]
    public async Task SessionCompletionCancelsAnAcceptedDoorbellAndQueuedInput()
    {
        using var terminal = new FakeTerminal();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var session = Session(terminal, delay: async token => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); });
        using var client = session.Connect();
        var ringing = client.SendAsync(new HostFrame(HostFrameKind.Ring, []));
        await entered.Task.WaitAsync(Deadline);
        var queued = client.SendAsync(new HostFrame(HostFrameKind.Input, [13]));
        session.Complete(0);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ringing.WaitAsync(Deadline));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(Deadline));
        Assert.Equal(AgentBriefing.Doorbell, Encoding.UTF8.GetString(Assert.Single(terminal.Written)));
    }

    [Theory]
    [InlineData(HostFrameKind.Hello)]
    [InlineData(HostFrameKind.Output)]
    [InlineData(HostFrameKind.Exited)]
    [InlineData((HostFrameKind)0)]
    public async Task ServerFramesCannotBeSentAsClientCommands(HostFrameKind kind)
    {
        using var terminal = new FakeTerminal();
        using var session = new AgentSession(terminal, Initial);
        using var client = session.Connect();
        await Assert.ThrowsAsync<InvalidDataException>(() => client.SendAsync(new HostFrame(kind, [])));
        Assert.Empty(terminal.Written);
        Assert.False(session.StopRequested.IsCompleted);
    }

    [Theory]
    [InlineData(HostFrameKind.Ring, 1)]
    [InlineData(HostFrameKind.Stop, 1)]
    [InlineData(HostFrameKind.Status, 1)]
    [InlineData(HostFrameKind.Input, HostProtocol.MaxPayload + 1)]
    [InlineData(HostFrameKind.Resize, 8)]
    public async Task MalformedCommandsDoNotReachTheTerminal(HostFrameKind kind, int length)
    {
        using var terminal = new FakeTerminal();
        using var session = new AgentSession(terminal, Initial);
        using var client = session.Connect();
        await Assert.ThrowsAsync<InvalidDataException>(() => client.SendAsync(new HostFrame(kind, new byte[length])));
        Assert.Empty(terminal.Written);
        Assert.Empty(terminal.Sizes);
        Assert.False(session.StopRequested.IsCompleted);
    }

    [Fact]
    public async Task DetachingDoesNotStopTheAgentAndStopRequestsReachTheHost()
    {
        using var terminal = new FakeTerminal();
        using var session = new AgentSession(terminal, Initial);
        using var detached = session.Connect();
        detached.Dispose();
        await detached.Frames.Completion.WaitAsync(Deadline);
        await Assert.ThrowsAsync<IOException>(() => detached.SendAsync(new HostFrame(HostFrameKind.Stop, [])));
        Assert.False(session.StopRequested.IsCompleted);
        using var controller = session.Connect();
        await controller.SendAsync(new HostFrame(HostFrameKind.Stop, []));
        await session.StopRequested.WaitAsync(Deadline);
        Assert.False(terminal.Stopped);
    }

    [Fact]
    public async Task ExitIsFinalAndDisposalIsIdempotent()
    {
        using var terminal = new FakeTerminal();
        var session = new AgentSession(terminal, Initial);
        using var client = session.Connect();
        session.Complete(17);
        session.Publish("too late"u8);
        session.Complete(99);
        await Assert.ThrowsAsync<IOException>(() => client.SendAsync(new HostFrame(HostFrameKind.Input, [13])));
        using var later = session.Connect();
        Assert.Equal("", await OutputBeforeExit(later, 17));
        session.Dispose();
        session.Dispose();
        Assert.Throws<ObjectDisposedException>(() => session.Connect());
        Assert.Equal("", await OutputBeforeExit(client, 17));
        Assert.False(terminal.Stopped);
    }

    [Fact]
    public async Task AnUnexpectedSessionDisposalReportsFailureAfterQueuedOutput()
    {
        using var terminal = new FakeTerminal();
        var session = new AgentSession(terminal, Initial);
        using var client = session.Connect();
        session.Publish("last bytes"u8);
        session.Dispose();
        Assert.Equal("last bytes", await OutputBeforeExit(client, -1));
    }

    [Fact]
    public async Task AnInputFrameCanFillTheWholeWirePayload()
    {
        using var terminal = new FakeTerminal();
        using var session = new AgentSession(terminal, Initial);
        using var client = session.Connect();
        var bytes = new byte[HostProtocol.MaxPayload];
        await client.SendAsync(new HostFrame(HostFrameKind.Input, bytes));
        Assert.Equal(bytes, Assert.Single(terminal.Written));
    }

    [Fact]
    public void InvalidSessionLimitsFailBeforeAnyClientConnects()
    {
        using var terminal = new FakeTerminal();
        Assert.Throws<InvalidDataException>(() => new AgentSession(terminal, new TerminalSize(0, 24)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Session(terminal, capacity: 0));
    }

    private static AgentSession Session(FakeTerminal terminal, int capacity = 128, int replayCapacity = TerminalReplay.DefaultCapacity,
        Func<CancellationToken, Task>? delay = null) => new(terminal, Initial, replayCapacity, capacity, delay ?? (_ => Task.CompletedTask));

    private static async Task<HostFrame> Next(AgentSession.Client client) => await client.Frames.ReadAsync().AsTask().WaitAsync(Deadline);

    private static async Task<string> OutputBeforeExit(AgentSession.Client client, int code)
    {
        using var output = new MemoryStream();
        using var deadline = new CancellationTokenSource(Deadline);
        var exited = false;
        await foreach (var frame in client.Frames.ReadAllAsync(deadline.Token))
        {
            Assert.InRange(frame.Payload.Length, 0, HostProtocol.MaxPayload);
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

    private sealed class FakeTerminal : IAgentTerminal
    {
        private readonly Pipe pipe = new();
        private readonly TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly RecordingStream input = new();
        public int Pid => 123;
        public Stream Input { get; }
        public Stream Output { get; }
        public Task<int> Exited => exited.Task;
        public List<TerminalSize> Sizes { get; } = [];
        public List<byte[]> Written => input.Writes;
        public IOException? WriteError { get => input.Error; set => input.Error = value; }
        public bool Stopped { get; private set; }
        public FakeTerminal(bool buffered = false)
        {
            Output = pipe.Reader.AsStream();
            Input = buffered ? new BufferedStream(input) : input;
        }
        public async Task Produce(byte[] bytes) => await pipe.Writer.WriteAsync(bytes);
        public Task EndOutput() => pipe.Writer.CompleteAsync().AsTask();
        public void Resize(TerminalSize size) => Sizes.Add(size);
        public void Stop() { Stopped = true; exited.TrySetResult(0); }
        public void Dispose() { Output.Dispose(); Input.Dispose(); input.Dispose(); pipe.Writer.Complete(); }
    }

    private sealed class RecordingStream : MemoryStream
    {
        public List<byte[]> Writes { get; } = [];
        public IOException? Error { get; set; }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Error is not null) throw Error;
            Writes.Add(buffer.ToArray());
            return ValueTask.CompletedTask;
        }
    }
}
