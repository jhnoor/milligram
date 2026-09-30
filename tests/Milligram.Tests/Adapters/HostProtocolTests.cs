using System.Buffers.Binary;
using System.Text;
using Milligram.Adapters.Companion;

namespace Milligram.Tests.Adapters;

public class HostProtocolTests
{
    [Fact]
    public async Task SeveralFramesRoundTripAcrossSingleByteReads()
    {
        HostFrame[] frames = [HostProtocol.Json(HostFrameKind.Hello, new HostHello(1, "0.2.0-beta.1")),
            new(HostFrameKind.Output, Encoding.UTF8.GetBytes("Grüße 🐱\u001b[31m")),
            new(HostFrameKind.Input, [3, 13]), HostProtocol.Resize(new TerminalSize(96, 31)),
            new(HostFrameKind.Ring, []), new(HostFrameKind.Stop, []), new(HostFrameKind.Status, []), HostProtocol.Exited(-17)];
        using var wire = new MemoryStream();
        foreach (var frame in frames) await HostProtocol.WriteAsync(wire, frame, CancellationToken.None);
        using var fragmented = new SingleByteStream(wire.ToArray());

        foreach (var expected in frames)
        {
            var actual = Assert.IsType<HostFrame>(await HostProtocol.ReadAsync(fragmented, CancellationToken.None));
            Assert.Equal(expected.Kind, actual.Kind);
            Assert.Equal(expected.Payload, actual.Payload);
        }
        Assert.Null(await HostProtocol.ReadAsync(fragmented, CancellationToken.None));
    }

    [Fact]
    public async Task WireIntegersAreLittleEndianAndTheLengthIncludesTheKind()
    {
        using var wire = new MemoryStream();
        await HostProtocol.WriteAsync(wire, HostProtocol.Resize(new TerminalSize(258, 515)), CancellationToken.None);

        Assert.Equal(new byte[] { 9, 0, 0, 0, 4, 2, 1, 0, 0, 3, 2, 0, 0 }, wire.ToArray());
        Assert.Equal(new byte[] { 0xEF, 0xFF, 0xFF, 0xFF }, HostProtocol.Exited(-17).Payload);
        Assert.Equal(-17, HostProtocol.ReadExitCode(HostProtocol.Exited(-17)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(HostProtocol.MaxPayload + 2)]
    [InlineData(int.MaxValue)]
    public async Task InvalidLengthsFailBeforeReadingTheBody(int length)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, length);
        using var wire = new SingleByteStream(header);

        await Assert.ThrowsAsync<InvalidDataException>(async () => await HostProtocol.ReadAsync(wire, CancellationToken.None));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task ATruncatedFrameCannotLookLikeACleanDisconnect(int length)
    {
        using var wire = new SingleByteStream(new byte[] { 2, 0, 0, 0, 2, 65 }[..length]);

        await Assert.ThrowsAsync<EndOfStreamException>(async () => await HostProtocol.ReadAsync(wire, CancellationToken.None));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(9, 0)]
    [InlineData(255, 0)]
    [InlineData(1, 0)]
    [InlineData(1, 513)]
    [InlineData(4, 7)]
    [InlineData(4, 9)]
    [InlineData(5, 1)]
    [InlineData(6, 1)]
    [InlineData(8, 3)]
    [InlineData(8, 5)]
    public async Task InvalidControlFramesAreRejectedWhenReadAndWritten(byte kind, int payloadLength)
    {
        using var output = new MemoryStream();
        var frame = new HostFrame((HostFrameKind)kind, new byte[payloadLength]);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await HostProtocol.WriteAsync(output, frame, CancellationToken.None));
        Assert.Empty(output.ToArray());
        var raw = new byte[payloadLength + 5];
        BinaryPrimitives.WriteInt32LittleEndian(raw, payloadLength + 1);
        raw[4] = kind;
        using var input = new SingleByteStream(raw);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await HostProtocol.ReadAsync(input, CancellationToken.None));
    }

    [Fact]
    public async Task TheMaximumDataPayloadFitsExactly()
    {
        using var wire = new MemoryStream();
        var frame = new HostFrame(HostFrameKind.Output, Enumerable.Repeat((byte)42, HostProtocol.MaxPayload).ToArray());
        await HostProtocol.WriteAsync(wire, frame, CancellationToken.None);
        wire.Position = 0;
        Assert.Equal(frame.Payload, (await HostProtocol.ReadAsync(wire, CancellationToken.None))!.Payload);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await HostProtocol.WriteAsync(wire, new HostFrame(HostFrameKind.Input, new byte[HostProtocol.MaxPayload + 1]), CancellationToken.None));
    }

    [Fact]
    public async Task TheGreetingLimitIsInclusiveAndWritingFlushesBufferedTransports()
    {
        using var wire = new MemoryStream();
        using var buffered = new BufferedStream(wire, 4096);
        var frame = new HostFrame(HostFrameKind.Hello, Enumerable.Repeat((byte)' ', 512).ToArray());
        await HostProtocol.WriteAsync(buffered, frame, CancellationToken.None);
        Assert.Equal(517, wire.Length);
        wire.Position = 0;
        Assert.Equal(frame.Payload, (await HostProtocol.ReadAsync(wire, CancellationToken.None))!.Payload);
    }

    [Theory]
    [InlineData(1, 24)]
    [InlineData(80, 1)]
    [InlineData(1001, 24)]
    [InlineData(80, 1001)]
    [InlineData(-1, 24)]
    [InlineData(80, int.MaxValue)]
    public void InvalidDimensionsAreRejectedOnBothSides(int columns, int rows)
    {
        Assert.Throws<InvalidDataException>(() => HostProtocol.Resize(new TerminalSize(columns, rows)));
        var payload = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(payload, columns);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4), rows);
        Assert.Throws<InvalidDataException>(() => HostProtocol.ReadSize(new HostFrame(HostFrameKind.Resize, payload)));
    }

    [Theory]
    [InlineData(2, 2)]
    [InlineData(1000, 1000)]
    public void BoundaryDimensionsAreAccepted(int columns, int rows)
    {
        var size = new TerminalSize(columns, rows);
        Assert.Equal(size, HostProtocol.ReadSize(HostProtocol.Resize(size)));
    }

    [Fact]
    public void TypedPayloadsRejectTheWrongKindAndUseTheSharedJsonConventions()
    {
        Assert.Throws<InvalidDataException>(() => HostProtocol.ReadSize(new HostFrame(HostFrameKind.Input, new byte[8])));
        Assert.Throws<InvalidDataException>(() => HostProtocol.ReadExitCode(new HostFrame(HostFrameKind.Input, new byte[4])));
        Assert.Throws<InvalidDataException>(() => HostProtocol.ReadExitCode(new HostFrame(HostFrameKind.Exited, [])));
        var hello = new HostHello(HostProtocol.Version, "0.2.0");
        var frame = HostProtocol.Json(HostFrameKind.Hello, hello);
        Assert.Contains("\"protocol\"", Encoding.UTF8.GetString(frame.Payload));
        Assert.Equal(hello, HostProtocol.ReadJson<HostHello>(frame));
        Assert.Equal(hello, HostProtocol.ReadHello(frame));
        Assert.Throws<InvalidDataException>(() => HostProtocol.ReadJson<HostHello>(new HostFrame(HostFrameKind.Hello, "null"u8.ToArray())));
        Assert.Throws<InvalidDataException>(() => HostProtocol.ReadJson<HostHello>(new HostFrame(HostFrameKind.Hello, "{"u8.ToArray())));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"protocol\":1}")]
    [InlineData("{\"protocol\":0,\"version\":\"0.2.0\"}")]
    [InlineData("{\"protocol\":-1,\"version\":\"0.2.0\"}")]
    [InlineData("{\"protocol\":1,\"version\":\" \"}")]
    public void IncompleteGreetingsAreRejected(string json) =>
        Assert.Throws<InvalidDataException>(() => HostProtocol.ReadHello(new HostFrame(HostFrameKind.Hello, Encoding.UTF8.GetBytes(json))));

    [Fact]
    public void NewerProtocolsCanBeReportedButInvalidGreetingKindsAndVersionsCannot()
    {
        var future = new HostHello(HostProtocol.Version + 1, new string('v', 128));
        Assert.Equal(future, HostProtocol.ReadHello(HostProtocol.Json(HostFrameKind.Hello, future)));
        Assert.Throws<InvalidDataException>(() => HostProtocol.ReadHello(HostProtocol.Json(HostFrameKind.Hello, future with { Version = new string('v', 129) })));
        Assert.Throws<InvalidDataException>(() => HostProtocol.ReadHello(HostProtocol.Json(HostFrameKind.Status, future)));
    }

    [Fact]
    public async Task CancellationReachesTheUnderlyingStream()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var stream = new MemoryStream([1, 0, 0, 0, 5]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await HostProtocol.ReadAsync(stream, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await HostProtocol.WriteAsync(stream, new HostFrame(HostFrameKind.Ring, []), cancelled.Token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0123456789abcdef0123456789abcdef")]
    public void GreetingsRetainTheOptionalInstanceForCompatiblePeers(string? instance)
    {
        var hello = new HostHello(1, "test", instance);
        Assert.Equal(hello, HostProtocol.ReadHello(HostProtocol.Json(HostFrameKind.Hello, hello)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("01234567-89ab-cdef-0123-456789abcdef")]
    public void MalformedInstancesAreRejected(string instance) =>
        Assert.Throws<InvalidDataException>(() => HostProtocol.ReadHello(HostProtocol.Json(HostFrameKind.Hello, new HostHello(1, "test", instance))));

    private sealed class SingleByteStream(byte[] data) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }
}
