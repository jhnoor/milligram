using System.Buffers.Binary;
using System.Text.Json;
using Milligram.Application;

namespace Milligram.Adapters.Companion;

public enum HostFrameKind : byte { Hello = 1, Output = 2, Input = 3, Resize = 4, Ring = 5, Stop = 6, Status = 7, Exited = 8 }

public sealed record HostFrame(HostFrameKind Kind, byte[] Payload);
public sealed record HostHello(int Protocol, string Version, string? Instance = null);
public sealed record TerminalSize(int Columns, int Rows);

/// <summary>Bounded, versioned frames on the local agent pipe; stream reads need not align with frames.</summary>
public static class HostProtocol
{
    public const int Version = 1;
    public const int MaxPayload = 16 * 1024;

    public static async ValueTask<HostFrame?> ReadAsync(Stream stream, CancellationToken cancellation)
    {
        var header = new byte[4];
        var read = await stream.ReadAsync(header, cancellation);
        if (read == 0) return null;
        await stream.ReadExactlyAsync(header.AsMemory(read), cancellation);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 1 or > MaxPayload + 1) throw new InvalidDataException("Invalid agent frame length.");
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, cancellation);
        var frame = new HostFrame((HostFrameKind)body[0], body[1..]);
        Validate(frame);
        return frame;
    }

    /// <summary>One writer owns a connection; concurrent frame writes must be serialized by its caller.</summary>
    public static async ValueTask WriteAsync(Stream stream, HostFrame frame, CancellationToken cancellation)
    {
        Validate(frame);
        var bytes = new byte[frame.Payload.Length + 5];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, frame.Payload.Length + 1);
        bytes[4] = (byte)frame.Kind;
        frame.Payload.CopyTo(bytes, 5);
        await stream.WriteAsync(bytes, cancellation);
        await stream.FlushAsync(cancellation);
    }

    public static HostFrame Json<T>(HostFrameKind kind, T value) =>
        new(kind, JsonSerializer.SerializeToUtf8Bytes(value, MilligramJson.Options));

    public static T ReadJson<T>(HostFrame frame) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(frame.Payload, MilligramJson.Options)
                ?? throw new InvalidDataException("Missing agent frame data.");
        }
        catch (JsonException e) { throw new InvalidDataException("Invalid agent frame data.", e); }
    }

    /// <summary>Keep a newer protocol readable so the client can explain why the host needs a restart.</summary>
    public static HostHello ReadHello(HostFrame frame)
    {
        if (frame.Kind != HostFrameKind.Hello) throw new InvalidDataException("Expected an agent greeting.");
        var hello = ReadJson<HostHello>(frame);
        if (hello.Protocol < 1 || string.IsNullOrWhiteSpace(hello.Version) || hello.Version.Length > 128)
            throw new InvalidDataException("Invalid agent greeting.");
        if (hello.Instance is not null && !Guid.TryParseExact(hello.Instance, "N", out _))
            throw new InvalidDataException("Invalid agent instance.");
        return hello;
    }

    public static HostFrame Resize(TerminalSize size)
    {
        ValidateSize(size);
        var payload = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(payload, size.Columns);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4), size.Rows);
        return new HostFrame(HostFrameKind.Resize, payload);
    }

    public static TerminalSize ReadSize(HostFrame frame)
    {
        if (frame.Kind != HostFrameKind.Resize || frame.Payload.Length != 8) throw new InvalidDataException("Invalid terminal resize frame.");
        var size = new TerminalSize(BinaryPrimitives.ReadInt32LittleEndian(frame.Payload), BinaryPrimitives.ReadInt32LittleEndian(frame.Payload.AsSpan(4)));
        ValidateSize(size);
        return size;
    }

    public static HostFrame Exited(int code)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(payload, code);
        return new HostFrame(HostFrameKind.Exited, payload);
    }

    public static int ReadExitCode(HostFrame frame) =>
        frame.Kind == HostFrameKind.Exited && frame.Payload.Length == 4
            ? BinaryPrimitives.ReadInt32LittleEndian(frame.Payload) : throw new InvalidDataException("Invalid terminal exit frame.");

    private static void ValidateSize(TerminalSize size)
    {
        if (size.Columns is < 2 or > 1000 || size.Rows is < 2 or > 1000) throw new InvalidDataException("Terminal dimensions must be between 2 and 1000.");
    }

    private static void Validate(HostFrame frame)
    {
        if (frame.Kind is < HostFrameKind.Hello or > HostFrameKind.Exited || frame.Payload.Length > MaxPayload)
            throw new InvalidDataException("Invalid agent frame.");
        switch (frame.Kind)
        {
            case HostFrameKind.Hello when frame.Payload.Length is 0 or > 512:
            case HostFrameKind.Ring or HostFrameKind.Stop when frame.Payload.Length != 0:
            case HostFrameKind.Exited when frame.Payload.Length != 4:
                throw new InvalidDataException("Invalid agent control frame.");
            case HostFrameKind.Resize:
                ReadSize(frame);
                break;
        }
    }
}
