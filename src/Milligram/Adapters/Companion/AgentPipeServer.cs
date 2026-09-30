using System.IO.Pipes;
using System.Net.Sockets;

namespace Milligram.Adapters.Companion;

/// <summary>Accepts same-user terminal clients; an incomplete greeting cannot hold a connection indefinitely.</summary>
public sealed class AgentPipeServer(AgentSession session, string endpoint, HostHello greeting, Action<string> log) : IDisposable
{
    private readonly CancellationTokenSource accepting = new();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int running;

    public Task Ready => ready.Task;
    internal TimeSpan GreetingTimeout { get; init; } = TimeSpan.FromSeconds(3);
    internal Func<NamedPipeServerStream, CancellationToken, Task> AcceptConnectionAsync { get; init; } =
        (pipe, cancellation) => pipe.WaitForConnectionAsync(cancellation);

    /// <summary>Stop accepting first; existing clients may drain an exit frame before the host cancels this task.</summary>
    public void StopAccepting() => accepting.Cancel();

    public async Task RunAsync(CancellationToken cancellation)
    {
        if (Interlocked.Exchange(ref running, 1) != 0) throw new InvalidOperationException("The agent pipe server is already running.");
        using var connections = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation, accepting.Token);
        var clients = new List<Task>();
        NamedPipeServerStream? waiting = null;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var pipe = waiting ?? CreatePipe();
                waiting = null;
                try
                {
                    ready.TrySetResult();
                    await AcceptConnectionAsync(pipe, stop.Token);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or SocketException)
                {
                    // Unix checks the peer's identity during accept, before our greeting handler runs.
                    try { if (!stop.IsCancellationRequested) waiting = CreatePipe(); }
                    finally { await pipe.DisposeAsync(); }
                    if (stop.IsCancellationRequested) break;
                    log("Agent connection rejected: " + error.Message);
                    await Task.Delay(20, stop.Token);
                    continue;
                }
                catch { await pipe.DisposeAsync(); throw; }
                // Keep the Unix listening socket alive even when this client disconnects synchronously.
                try { waiting = CreatePipe(); }
                catch { await pipe.DisposeAsync(); throw; }
                clients.RemoveAll(task => task.IsCompletedSuccessfully);
                clients.Add(ServeAsync(pipe, connections.Token));
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception e)
        {
            ready.TrySetException(e);
            connections.Cancel();
            throw;
        }
        finally
        {
            if (waiting is not null) await waiting.DisposeAsync();
            ready.TrySetCanceled();
            await Task.WhenAll(clients);
        }
    }

    private NamedPipeServerStream CreatePipe() => new(endpoint, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, HostProtocol.MaxPayload, HostProtocol.MaxPayload);

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken cancellation)
    {
        await using var connection = pipe;
        try
        {
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                deadline.CancelAfter(GreetingTimeout);
                await HostProtocol.WriteAsync(pipe, HostProtocol.Json(HostFrameKind.Hello, greeting), deadline.Token);
                var frame = await HostProtocol.ReadAsync(pipe, deadline.Token) ?? throw new EndOfStreamException("Missing agent client greeting.");
                if (HostProtocol.ReadHello(frame).Protocol != greeting.Protocol) throw new InvalidDataException("Incompatible agent client protocol.");
            }
            using var client = session.Connect();
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation, client.Disconnected);
            var sending = SendAsync(pipe, client, stop.Token);
            var receiving = ReceiveAsync(pipe, client, stop.Token);
            try { await await Task.WhenAny(sending, receiving); }
            finally
            {
                stop.Cancel();
                try { await Task.WhenAll(sending, receiving); }
                catch (Exception e) when (IsDisconnect(e)) { }
            }
        }
        catch (Exception e) when (IsDisconnect(e))
        {
            if (e is not OperationCanceledException) log($"Agent client disconnected: {e.Message}");
        }
    }

    private static async Task SendAsync(Stream pipe, AgentSession.Client client, CancellationToken cancellation)
    {
        await foreach (var frame in client.Frames.ReadAllAsync(cancellation)) await HostProtocol.WriteAsync(pipe, frame, cancellation);
    }

    private static async Task ReceiveAsync(Stream pipe, AgentSession.Client client, CancellationToken cancellation)
    {
        while (await HostProtocol.ReadAsync(pipe, cancellation) is { } frame) await client.SendAsync(frame, cancellation);
    }

    private static bool IsDisconnect(Exception error) => error is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException;

    public void Dispose()
    {
        accepting.Cancel();
        accepting.Dispose();
    }
}
