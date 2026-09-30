using System.Net.WebSockets;
using System.Text.Json;
using Milligram.Adapters.Companion;
using Milligram.Application;

namespace Milligram.Adapters.Web;

public sealed record BrowserTerminalMessage(HostFrame? Frame, int Acknowledged = 0);

/// <summary>Browser messages have bounded size and assembly time; idle terminals remain connected.</summary>
public static class BrowserTerminalProtocol
{
    public static async Task<BrowserTerminalMessage?> ReadAsync(WebSocket socket, CancellationToken cancellation)
    {
        var buffer = new byte[HostProtocol.MaxPayload + 1];
        var length = 0;
        using var message = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        for (var fragments = 0; ; fragments++)
        {
            var part = await socket.ReceiveAsync(buffer.AsMemory(length), message.Token);
            if (part.MessageType == WebSocketMessageType.Close) return null;
            length += part.Count;
            var limit = part.MessageType == WebSocketMessageType.Text ? 512 : HostProtocol.MaxPayload;
            if (length > limit || fragments >= 128)
                throw new BrowserTerminalException(WebSocketCloseStatus.MessageTooBig, "Terminal message is too large.");
            if (part.EndOfMessage)
                return part.MessageType == WebSocketMessageType.Binary
                    ? new(new HostFrame(HostFrameKind.Input, buffer[..length])) : Control(buffer.AsSpan(0, length));
            if (fragments == 0) message.CancelAfter(TimeSpan.FromSeconds(3));
        }
    }

    private sealed record Command(string? Type, int Columns, int Rows, int Bytes);

    private static BrowserTerminalMessage Control(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var command = JsonSerializer.Deserialize<Command>(bytes, MilligramJson.Compact);
            return command?.Type switch
            {
                "resize" => new(HostProtocol.Resize(new TerminalSize(command.Columns, command.Rows))),
                "status" => new(new HostFrame(HostFrameKind.Status, [])),
                "ack" => new(null, command.Bytes),
                _ => throw new InvalidDataException(),
            };
        }
        catch (Exception e) when (e is JsonException or InvalidDataException)
        {
            throw new BrowserTerminalException(WebSocketCloseStatus.InvalidPayloadData, "Invalid terminal control.");
        }
    }

    public static byte[] Status(HostFrame frame)
    {
        var status = HostProtocol.ReadJson<AgentHostStatus>(frame);
        return JsonSerializer.SerializeToUtf8Bytes(new { type = "status", status.Pid, status.Clients, status.Columns, status.Rows }, MilligramJson.Compact);
    }

    public static byte[] Exited(HostFrame frame) =>
        JsonSerializer.SerializeToUtf8Bytes(new { type = "exited", code = HostProtocol.ReadExitCode(frame) }, MilligramJson.Compact);
}

public sealed class BrowserTerminalException(WebSocketCloseStatus status, string message) : Exception(message)
{
    public WebSocketCloseStatus Status { get; } = status;
}
