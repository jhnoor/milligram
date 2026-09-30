using System.IO.Pipelines;
using System.Net.WebSockets;
using System.Threading.Channels;
using Milligram.Adapters.Companion;
using Milligram.Adapters.Web;

namespace Milligram.Tests.Adapters;

public class AgentWebRelayTests
{
    [Theory]
    [InlineData("browser")]
    [InlineData("host")]
    [InlineData("close")]
    public async Task StalledPeersCannotHoldRelayTasksPastTheirDeadlines(string stalled)
    {
        using var socket = new Socket { StallSend = stalled == "browser", AcknowledgeClose = stalled != "close" };
        await using var transport = new Transport { StallWrite = stalled == "host" };
        await using var pipe = new AgentPipeClient(transport);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        if (stalled == "host") socket.Messages.Writer.TryWrite(new([65], WebSocketMessageType.Binary, true));
        else await HostProtocol.WriteAsync(transport.Host, stalled == "close" ? HostProtocol.Exited(17) : new HostFrame(HostFrameKind.Output, [65]), timeout.Token);
        await AgentWebRelay.RunAsync(socket, pipe, CancellationToken.None).WaitAsync(timeout.Token);
        Assert.Equal(WebSocketState.Aborted, socket.State);
        Assert.Equal(0, socket.Readers);
        Assert.Equal(0, socket.Writers);
        Assert.Equal(0, transport.Writers);
        Assert.Equal(0, transport.Readers);
        if (stalled == "browser") Assert.Equal(1, socket.CancelledSends);
        else if (stalled == "host")
        {
            Assert.Equal(1, transport.CancelledWrites);
            Assert.False(string.IsNullOrWhiteSpace(socket.CloseStatusDescription));
        }
        else Assert.Equal(WebSocketCloseStatus.NormalClosure, socket.CloseStatus);
    }

    [Fact]
    public async Task ClosingWaitsForTheCurrentOutputWriteBeforeSendingTheCloseFrame()
    {
        using var socket = new Socket { DelaySend = true };
        await using var transport = new Transport();
        await using var pipe = new AgentPipeClient(transport);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await HostProtocol.WriteAsync(transport.Host, new HostFrame(HostFrameKind.Output, [65]), timeout.Token);
        var running = AgentWebRelay.RunAsync(socket, pipe, timeout.Token);
        await socket.Sending.Task.WaitAsync(timeout.Token);
        socket.Messages.Writer.TryWrite(new([], WebSocketMessageType.Close, true));
        await running;
        Assert.False(socket.ClosedDuringSend);
        Assert.Equal(0, socket.Writers);
    }

    [Fact]
    public async Task CancellationWaitsForTheReaderToFinishCleaningUp()
    {
        using var socket = new Socket { DelayCleanup = true };
        await using var transport = new Transport();
        await using var pipe = new AgentPipeClient(transport);
        using var cancellation = new CancellationTokenSource();
        var running = AgentWebRelay.RunAsync(socket, pipe, cancellation.Token);
        await socket.Reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, socket.Readers);
        Assert.Equal(0, transport.Readers);
    }

    [Fact]
    public async Task BrokenBrowserReadsAreContainedAndJoinTheHostReader()
    {
        using var socket = new Socket { ReadFails = true };
        await using var transport = new Transport();
        await using var pipe = new AgentPipeClient(transport);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await AgentWebRelay.RunAsync(socket, pipe, timeout.Token);
        Assert.Equal(WebSocketCloseStatus.InternalServerError, socket.CloseStatus);
        Assert.Equal(0, socket.Readers);
        Assert.Equal(0, transport.Readers);
    }

    [Fact]
    public async Task FragmentFloodsAreBoundedEvenWhenTheyContainNoBytes()
    {
        using var socket = new Socket();
        for (var i = 0; i < 128; i++) socket.Messages.Writer.TryWrite(new([], WebSocketMessageType.Binary, false));
        socket.Messages.Writer.TryWrite(new([], WebSocketMessageType.Binary, true));
        var error = await Assert.ThrowsAsync<BrowserTerminalException>(() => BrowserTerminalProtocol.ReadAsync(socket, CancellationToken.None));
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, error.Status);
        Assert.Equal(0, socket.Readers);
    }

    [Fact]
    public async Task APartialMessageHasADeadlineEvenWhenTheNextFragmentNeverArrives()
    {
        using var socket = new Socket();
        socket.Messages.Writer.TryWrite(new([65], WebSocketMessageType.Binary, false));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BrowserTerminalProtocol.ReadAsync(socket, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, socket.Readers);
    }

    [Fact]
    public async Task IdleTerminalsCanWaitLongerThanTheMessageAssemblyDeadline()
    {
        using var socket = new Socket();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        var reading = BrowserTerminalProtocol.ReadAsync(socket, timeout.Token);
        await Task.Delay(TimeSpan.FromSeconds(3.1), timeout.Token);
        socket.Messages.Writer.TryWrite(new([65], WebSocketMessageType.Binary, true));
        Assert.Equal(new byte[] { 65 }, (await reading)!.Frame!.Payload);
        Assert.Equal(0, socket.Readers);
    }

    private sealed record Message(byte[] Bytes, WebSocketMessageType Type, bool End);

    private sealed class Socket : WebSocket
    {
        private WebSocketState state = WebSocketState.Open;
        private WebSocketCloseStatus? status;
        private string? description;
        public Channel<Message> Messages { get; } = Channel.CreateUnbounded<Message>();
        public TaskCompletionSource Reading { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Sending { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool StallSend { get; init; }
        public bool ReadFails { get; init; }
        public bool DelaySend { get; init; }
        public bool DelayCleanup { get; init; }
        public bool ClosedDuringSend { get; private set; }
        public bool AcknowledgeClose { get; init; } = true;
        public int Readers;
        public int Writers;
        public int CancelledSends;
        public override WebSocketCloseStatus? CloseStatus => status;
        public override string? CloseStatusDescription => description;
        public override WebSocketState State => state;
        public override string? SubProtocol => null;
        public override void Abort() => state = WebSocketState.Aborted;
        public override void Dispose() => Abort();
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            ClosedDuringSend = Writers > 0;
            status = closeStatus;
            description = statusDescription;
            state = WebSocketState.Closed;
            return Task.CompletedTask;
        }
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            ClosedDuringSend = Writers > 0;
            status = closeStatus;
            description = statusDescription;
            state = WebSocketState.CloseSent;
            if (AcknowledgeClose) Messages.Writer.TryWrite(new([], WebSocketMessageType.Close, true));
            return Task.CompletedTask;
        }
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Readers);
            Reading.TrySetResult();
            try
            {
                if (ReadFails) throw new WebSocketException("Browser disconnected.");
                var message = await Messages.Reader.ReadAsync(cancellationToken);
                message.Bytes.AsSpan().CopyTo(buffer.AsSpan());
                return new WebSocketReceiveResult(message.Bytes.Length, message.Type, message.End);
            }
            finally
            {
                if (DelayCleanup) await Task.Delay(100);
                Interlocked.Decrement(ref Readers);
            }
        }
        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Writers);
            Sending.TrySetResult();
            try
            {
                if (StallSend) await Task.Delay(Timeout.Infinite, cancellationToken);
                if (DelaySend) await Task.Delay(100, cancellationToken);
            }
            catch (OperationCanceledException) { CancelledSends++; Abort(); throw; }
            finally { Interlocked.Decrement(ref Writers); }
        }
    }

    private sealed class Transport : MemoryStream
    {
        private readonly Pipe output = new();
        public Stream Host => output.Writer.AsStream();
        public bool StallWrite { get; init; }
        public int Readers;
        public int Writers;
        public int CancelledWrites;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Readers);
            try { return await output.Reader.AsStream().ReadAsync(buffer, cancellationToken); }
            finally { Interlocked.Decrement(ref Readers); }
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Writers);
            try
            {
                if (StallWrite) await Task.Delay(Timeout.Infinite, cancellationToken);
                else await base.WriteAsync(buffer, cancellationToken);
            }
            catch (OperationCanceledException) { CancelledWrites++; throw; }
            finally { Interlocked.Decrement(ref Writers); }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { output.Reader.Complete(); output.Writer.Complete(); }
            base.Dispose(disposing);
        }
    }
}
