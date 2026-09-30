using System.IO.Pipes;
using System.Text;
using System.Threading.Channels;
using Milligram.Adapters.Companion;
using Milligram.Application;

namespace Milligram.Tests.Adapters;

public class AgentAttachmentTests
{
    [Fact]
    public async Task AttachmentRelaysBytesAndResizesAndDetachesWithoutStoppingTheHost()
    {
        await using var fixture = new Fixture();
        var running = fixture.Attachment.RunAsync(fixture.Token);
        await fixture.Greet();
        Assert.Equal(new TerminalSize(80, 24), HostProtocol.ReadSize((await fixture.Read())!));
        await fixture.Send(new HostFrame(HostFrameKind.Output, Encoding.UTF8.GetBytes("before 漢 🐱")));
        Assert.Equal("before 漢 🐱", Encoding.UTF8.GetString(await fixture.Terminal.Output.Reader.ReadAsync(fixture.Token)));
        fixture.Terminal.Input.Writer.TryWrite("typed\u0003\r"u8.ToArray());
        var input = (await fixture.Read())!;
        Assert.Equal(HostFrameKind.Input, input.Kind);
        Assert.Equal("typed\u0003\r"u8.ToArray(), input.Payload);
        fixture.Terminal.Resize(new TerminalSize(96, 31));
        Assert.Equal(new TerminalSize(96, 31), HostProtocol.ReadSize((await fixture.Read())!));
        fixture.Terminal.Input.Writer.TryWrite([0x1d]);
        fixture.Terminal.Input.Writer.TryWrite([(byte)'d']);
        Assert.Equal(0, await running.WaitAsync(fixture.Token));
        Assert.Null(await fixture.Read());
        Assert.True(fixture.Terminal.Disposed);
        Assert.Equal(0, fixture.Terminal.Readers);
    }

    [Theory]
    [InlineData("exit")]
    [InlineData("disconnect")]
    [InlineData("write failure")]
    [InlineData("interrupt")]
    [InlineData("eof")]
    [InlineData("invalid frame")]
    public async Task EveryExitPathJoinsTheConsoleReaderBeforeRestoringModes(string ending)
    {
        await using var fixture = new Fixture();
        var running = fixture.Attachment.RunAsync(fixture.Token);
        await fixture.Greet();
        Assert.Equal(HostFrameKind.Resize, (await fixture.Read())!.Kind);
        await fixture.Terminal.Reading.Task.WaitAsync(fixture.Token);
        switch (ending)
        {
            case "exit": await fixture.Send(HostProtocol.Exited(17)); break;
            case "disconnect": fixture.Pipe.Dispose(); break;
            case "write failure":
                fixture.Terminal.WriteFails = true;
                await fixture.Send(new HostFrame(HostFrameKind.Output, [65]));
                break;
            case "interrupt": fixture.Terminal.Interrupt(); break;
            case "eof": fixture.Terminal.Input.Writer.TryComplete(); break;
            case "invalid frame": await fixture.Send(new HostFrame(HostFrameKind.Input, [65])); break;
        }
        if (ending is "disconnect" or "write failure" or "invalid frame")
        {
            var message = (await Assert.ThrowsAsync<MilligramException>(() => running.WaitAsync(fixture.Token))).Message;
            Assert.Contains("Could not attach", message);
            Assert.Contains(ending switch { "disconnect" => "disconnected", "write failure" => "console closed", _ => "Unexpected" }, message);
        }
        else Assert.Equal(ending == "exit" ? 17 : 0, await running.WaitAsync(fixture.Token));
        Assert.True(fixture.Terminal.Disposed);
        Assert.Equal(0, fixture.Terminal.Readers);
    }

    [Fact]
    public async Task CancellingTheGreetingNeverChangesConsoleModes()
    {
        using var lifetime = new CancellationTokenSource();
        var opened = false;
        var attachment = new AgentAttachment(async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException();
        }, () => { opened = true; throw new InvalidOperationException(); });
        var running = attachment.RunAsync(lifetime.Token);
        lifetime.Cancel();
        Assert.Equal(0, await running);
        Assert.False(opened);
    }

    [Fact]
    public async Task AnUnresponsiveHostCannotLeaveAttachmentWaitingIndefinitely()
    {
        var attachment = new AgentAttachment(async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException();
        }, () => throw new InvalidOperationException("The console must stay untouched."));
        var error = await Assert.ThrowsAsync<MilligramException>(() => attachment.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("agent start", error.Message);
    }

    [Fact]
    public async Task AFullConsoleReadAndPendingDetachPrefixStillFitTheWireFrame()
    {
        await using var fixture = new Fixture();
        var running = fixture.Attachment.RunAsync(fixture.Token);
        await fixture.Greet();
        Assert.Equal(HostFrameKind.Resize, (await fixture.Read())!.Kind);
        fixture.Terminal.Input.Writer.TryWrite([0x1d]);
        var text = Enumerable.Repeat((byte)'x', HostProtocol.MaxPayload * 2).ToArray();
        fixture.Terminal.Input.Writer.TryWrite(text);
        var received = new List<byte>();
        while (received.Count < text.Length + 1)
        {
            var frame = (await fixture.Read())!;
            Assert.Equal(HostFrameKind.Input, frame.Kind);
            received.AddRange(frame.Payload);
        }
        Assert.Equal(new byte[] { 0x1d }.Concat(text), received);
        fixture.Terminal.Input.Writer.TryWrite([0x1d, (byte)'d']);
        Assert.Equal(0, await running.WaitAsync(fixture.Token));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string endpoint = "mg-attach-" + Guid.NewGuid().ToString("N")[..16];
        private readonly CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(5));
        public Local Terminal { get; } = new();
        public NamedPipeServerStream Pipe { get; }
        public AgentAttachment Attachment { get; }
        public CancellationToken Token => lifetime.Token;
        public Fixture()
        {
            Pipe = new NamedPipeServerStream(endpoint, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            Attachment = new AgentAttachment(token => AgentPipeClient.ConnectAsync(endpoint, new HostHello(1, "test"), token), () => Terminal);
        }
        public async Task Greet()
        {
            await Pipe.WaitForConnectionAsync(Token);
            Assert.Equal(HostFrameKind.Hello, (await Read())!.Kind);
            await Send(HostProtocol.Json(HostFrameKind.Hello, new HostHello(1, "test")));
        }
        public ValueTask<HostFrame?> Read() => HostProtocol.ReadAsync(Pipe, Token);
        public ValueTask Send(HostFrame frame) => HostProtocol.WriteAsync(Pipe, frame, Token);
        public async ValueTask DisposeAsync() { lifetime.Cancel(); await Pipe.DisposeAsync(); lifetime.Dispose(); }
    }

    private sealed class Local : ILocalTerminal
    {
        private readonly Queue<byte> pending = [];
        public Channel<byte[]> Input { get; } = Channel.CreateUnbounded<byte[]>();
        public Channel<byte[]> Output { get; } = Channel.CreateUnbounded<byte[]>();
        public TaskCompletionSource Reading { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TerminalSize Size { get; private set; } = new(80, 24);
        public event Action? Resized;
        public event Action? Interrupted;
        public bool Disposed { get; private set; }
        public bool WriteFails { get; set; }
        public int Readers;
        public int Read(byte[] buffer, CancellationToken cancellation)
        {
            Interlocked.Increment(ref Readers);
            try
            {
                Reading.TrySetResult();
                if (pending.Count == 0)
                {
                    byte[] bytes;
                    try { bytes = Input.Reader.ReadAsync(cancellation).AsTask().GetAwaiter().GetResult(); }
                    catch (ChannelClosedException) { return 0; }
                    foreach (var value in bytes) pending.Enqueue(value);
                }
                var count = Math.Min(buffer.Length, pending.Count);
                for (var i = 0; i < count; i++) buffer[i] = pending.Dequeue();
                return count;
            }
            finally { Interlocked.Decrement(ref Readers); }
        }
        public void Write(byte[] bytes)
        {
            if (WriteFails) throw new IOException("console closed");
            Output.Writer.TryWrite(bytes);
        }
        public void Resize(TerminalSize size) { Size = size; Resized?.Invoke(); }
        public void Interrupt() => Interrupted?.Invoke();
        public void Dispose() { Assert.Equal(0, Readers); Assert.Null(Resized); Assert.Null(Interrupted); Disposed = true; }
    }
}
