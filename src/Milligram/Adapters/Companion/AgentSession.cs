using System.Text;
using System.Threading.Channels;
using Milligram.Application;

namespace Milligram.Adapters.Companion;

public sealed record AgentHostStatus(int Pid, int Clients, int Columns, int Rows);

/// <summary>Serializes terminal input and hands replay over to live output without blocking on slow clients.</summary>
public sealed class AgentSession : IDisposable
{
    private readonly IAgentTerminal terminal;
    private readonly TerminalReplay replay;
    private readonly int queueCapacity;
    private readonly Func<CancellationToken, Task> ringDelay;
    private readonly Lock outputGate = new();
    private readonly SemaphoreSlim inputGate = new(1);
    private readonly HashSet<Client> clients = [];
    private readonly CancellationTokenSource lifetime = new();
    private readonly TaskCompletionSource stopRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TerminalSize size;
    private int? exitCode;
    private int copying;
    private bool disposed;

    public AgentSession(IAgentTerminal terminal, TerminalSize initialSize)
        : this(terminal, initialSize, TerminalReplay.DefaultCapacity, 128, token => Task.Delay(150, token)) { }

    internal AgentSession(IAgentTerminal terminal, TerminalSize initialSize, int replayCapacity, int queueCapacity,
        Func<CancellationToken, Task> ringDelay)
    {
        HostProtocol.Resize(initialSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(queueCapacity);
        this.terminal = terminal;
        replay = new TerminalReplay(replayCapacity);
        this.queueCapacity = queueCapacity;
        this.ringDelay = ringDelay;
        size = initialSize;
    }

    public Task StopRequested => stopRequested.Task;

    public Client Connect()
    {
        lock (outputGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var client = new Client(this, queueCapacity);
            clients.Add(client);
            var snapshot = replay.Snapshot();
            client.WaitingForBoundary = snapshot.Length == 0 && !replay.IsAtBoundary;
            for (var offset = 0; offset < snapshot.Length; offset += HostProtocol.MaxPayload)
                Enqueue(client, new HostFrame(HostFrameKind.Output, snapshot[offset..Math.Min(offset + HostProtocol.MaxPayload, snapshot.Length)]));
            if (exitCode is { } code)
            {
                Enqueue(client, HostProtocol.Exited(code));
                client.Queue.Writer.TryComplete();
                clients.Remove(client);
            }
            return client;
        }
    }

    /// <summary>The host waits for this pump to drain before announcing exit, then disposes the terminal itself.</summary>
    public async Task CopyOutputAsync(CancellationToken cancellation)
    {
        if (Interlocked.Exchange(ref copying, 1) != 0) throw new InvalidOperationException("Terminal output already has a reader.");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime.Token);
        var buffer = new byte[HostProtocol.MaxPayload];
        while (await terminal.Output.ReadAsync(buffer, stop.Token) is var read && read != 0)
            Publish(buffer.AsSpan(0, read));
    }

    internal void Publish(ReadOnlySpan<byte> output)
    {
        lock (outputGate)
        {
            if (exitCode is not null) return;
            while (!output.IsEmpty)
            {
                var chunk = output[..Math.Min(output.Length, HostProtocol.MaxPayload)];
                var firstSafe = replay.Append(chunk);
                var whole = new HostFrame(HostFrameKind.Output, chunk.ToArray());
                var tail = firstSafe == 0 ? whole : new HostFrame(HostFrameKind.Output, chunk[firstSafe..].ToArray());
                foreach (var client in clients.ToArray())
                {
                    var frame = client.WaitingForBoundary ? tail : whole;
                    if (client.WaitingForBoundary) client.WaitingForBoundary = firstSafe == chunk.Length && !replay.IsAtBoundary;
                    if (frame.Payload.Length > 0) Enqueue(client, frame);
                }
                output = output[chunk.Length..];
            }
        }
    }

    /// <summary>Output already queued stays ahead of exit; a healthy client can drain before its pipe closes.</summary>
    public void Complete(int code)
    {
        lock (outputGate)
        {
            if (exitCode is not null) return;
            exitCode = code;
            foreach (var client in clients.ToArray())
            {
                Enqueue(client, HostProtocol.Exited(code));
                client.Queue.Writer.TryComplete();
            }
            clients.Clear();
        }
        lifetime.Cancel();
    }

    public void Dispose()
    {
        lock (outputGate)
        {
            if (disposed) return;
            disposed = true;
        }
        Complete(-1);
        lifetime.Dispose();
    }

    private async Task ReceiveAsync(Client client, HostFrame frame, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        lock (outputGate)
        {
            RequireConnected(client);
            if (frame.Kind == HostFrameKind.Status && frame.Payload.Length == 0)
            {
                Enqueue(client, HostProtocol.Json(HostFrameKind.Status, new AgentHostStatus(terminal.Pid, clients.Count, size.Columns, size.Rows)));
                return;
            }
            if (frame.Kind == HostFrameKind.Stop && frame.Payload.Length == 0)
            {
                stopRequested.TrySetResult();
                return;
            }
        }
        var requestedSize = frame.Kind == HostFrameKind.Resize ? HostProtocol.ReadSize(frame) : null;
        if (frame.Kind is not (HostFrameKind.Input or HostFrameKind.Resize or HostFrameKind.Ring) ||
            frame.Payload.Length > HostProtocol.MaxPayload || frame.Kind == HostFrameKind.Ring && frame.Payload.Length != 0)
            throw new InvalidDataException("Unexpected agent client frame.");

        var terminalCancellation = lifetime.Token;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation, terminalCancellation, client.Disconnected);
        await inputGate.WaitAsync(stop.Token);
        try
        {
            terminalCancellation.ThrowIfCancellationRequested();
            stop.Token.ThrowIfCancellationRequested();
            lock (outputGate) RequireConnected(client);
            if (requestedSize is not null)
            {
                Activate(requestedSize, redraw: client.Size is null);
                client.Size = requestedSize;
            }
            else if (frame.Kind == HostFrameKind.Input)
            {
                if (client.Size is { } activeSize) Activate(activeSize, redraw: false);
                await terminal.Input.WriteAsync(frame.Payload, stop.Token);
                await terminal.Input.FlushAsync(stop.Token);
            }
            else
            {
                await terminal.Input.WriteAsync(Encoding.UTF8.GetBytes(AgentBriefing.Doorbell), terminalCancellation);
                await terminal.Input.FlushAsync(terminalCancellation);
                await ringDelay(terminalCancellation);
                await terminal.Input.WriteAsync("\r"u8.ToArray(), terminalCancellation);
                await terminal.Input.FlushAsync(terminalCancellation);
            }
        }
        finally { inputGate.Release(); }
    }

    private void Activate(TerminalSize requested, bool redraw)
    {
        TerminalSize current;
        lock (outputGate) current = size;
        if (redraw) terminal.Resize(requested with { Columns = requested.Columns == 1000 ? 999 : requested.Columns + 1 });
        if (redraw || requested != current) terminal.Resize(requested);
        lock (outputGate) size = requested;
    }

    private void Enqueue(Client client, HostFrame frame)
    {
        if (!clients.Contains(client)) return;
        if (!client.Queue.Writer.TryWrite(frame))
            Disconnect(client, new IOException("The agent client could not keep up with terminal output."));
    }

    private void RequireConnected(Client client)
    {
        if (!clients.Contains(client) || exitCode is not null || disposed) throw new IOException("The agent client is disconnected.");
    }

    private void Disconnect(Client client, Exception? error = null)
    {
        lock (outputGate)
        {
            clients.Remove(client);
            client.Queue.Writer.TryComplete(error);
            client.Cancel();
        }
    }

    /// <summary>A bounded output subscription; disposing it detaches without stopping the agent.</summary>
    public sealed class Client : IDisposable
    {
        private readonly AgentSession session;
        private readonly CancellationTokenSource disconnected = new();
        internal Channel<HostFrame> Queue { get; }
        internal TerminalSize? Size { get; set; }
        internal bool WaitingForBoundary { get; set; }

        internal Client(AgentSession session, int capacity)
        {
            this.session = session;
            Queue = Channel.CreateBounded<HostFrame>(new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                AllowSynchronousContinuations = false,
            });
        }

        public ChannelReader<HostFrame> Frames => Queue.Reader;
        public CancellationToken Disconnected => disconnected.Token;
        public Task SendAsync(HostFrame frame, CancellationToken cancellation = default) => session.ReceiveAsync(this, frame, cancellation);
        internal void Cancel() => _ = disconnected.CancelAsync();
        public void Dispose() => session.Disconnect(this);
    }
}
