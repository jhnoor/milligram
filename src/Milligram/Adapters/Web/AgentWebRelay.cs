using System.Net.WebSockets;
using Milligram.Adapters.Companion;

namespace Milligram.Adapters.Web;

/// <summary>One reader and writer per socket, with bounded shutdown and no agent stop on detach.</summary>
public static class AgentWebRelay
{
    private sealed record Ending(WebSocketCloseStatus Status, string Reason);
    private static readonly Ending Detached = new(WebSocketCloseStatus.NormalClosure, "Detached.");

    public static async Task RunAsync(WebSocket socket, AgentPipeClient pipe, CancellationToken cancellation)
    {
        using var inputStop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        using var outputStop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var input = ReceiveAsync(socket, pipe, inputStop.Token);
        var output = OutputAsync(socket, pipe, outputStop.Token, cancellation);
        try
        {
            var ending = await await Task.WhenAny(input, output);
            outputStop.Cancel();
            await output;
            using var close = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            close.CancelAfter(TimeSpan.FromSeconds(3));
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                if (input.IsCompleted) await socket.CloseAsync(ending.Status, ending.Reason, close.Token);
                else
                {
                    await socket.CloseOutputAsync(ending.Status, ending.Reason, close.Token);
                    await input.WaitAsync(close.Token);
                }
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException) { }
        finally
        {
            inputStop.Cancel();
            outputStop.Cancel();
            socket.Abort();
            await Task.WhenAll(input, output);
        }
    }

    private static async Task<Ending> ReceiveAsync(WebSocket socket, AgentPipeClient pipe, CancellationToken cancellation)
    {
        try
        {
            while (await BrowserTerminalProtocol.ReadAsync(socket, cancellation) is { } frame)
            {
                using var send = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                send.CancelAfter(TimeSpan.FromSeconds(3));
                await pipe.SendAsync(frame, send.Token);
            }
            return Detached;
        }
        catch (BrowserTerminalException e) { return new Ending(e.Status, e.Message); }
        catch (Exception e) when (e is IOException or InvalidDataException or WebSocketException or OperationCanceledException)
        {
            return new Ending(WebSocketCloseStatus.InternalServerError, "Terminal connection interrupted.");
        }
    }

    private static async Task<Ending> OutputAsync(WebSocket socket, AgentPipeClient pipe, CancellationToken reading, CancellationToken cancellation)
    {
        try
        {
            while (await pipe.ReadAsync(reading) is { } frame)
            {
                var bytes = frame.Kind switch
                {
                    HostFrameKind.Output => frame.Payload,
                    HostFrameKind.Status => BrowserTerminalProtocol.Status(frame),
                    HostFrameKind.Exited => BrowserTerminalProtocol.Exited(frame),
                    _ => throw new InvalidDataException(),
                };
                using var send = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                send.CancelAfter(TimeSpan.FromSeconds(3));
                await socket.SendAsync(bytes.AsMemory(), frame.Kind == HostFrameKind.Output ? WebSocketMessageType.Binary : WebSocketMessageType.Text,
                    endOfMessage: true, send.Token);
                if (frame.Kind == HostFrameKind.Exited) return new Ending(WebSocketCloseStatus.NormalClosure, "Agent exited.");
            }
            return new Ending(WebSocketCloseStatus.InternalServerError, "Agent host disconnected.");
        }
        catch (Exception e) when (e is IOException or InvalidDataException or WebSocketException or OperationCanceledException)
        {
            return new Ending(WebSocketCloseStatus.InternalServerError, "Terminal connection interrupted.");
        }
    }
}
