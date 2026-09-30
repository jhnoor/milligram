using System.Text;
using Milligram.Adapters.Companion;

namespace Milligram.Tests.Adapters;

public class TerminalReplayTests
{
    [Fact]
    public void OutputIsPreservedAcrossChunksAndSnapshotsAreIndependent()
    {
        var replay = new TerminalReplay(100);
        var output = Encoding.UTF8.GetBytes("Grüße ☃ 漢字 🐱\u001b[31mred\u001b[0m");
        foreach (var value in output) replay.Append([value]);
        var snapshot = replay.Snapshot();
        Assert.Equal(output, snapshot);
        snapshot[0] = 0;
        Assert.Equal(output, replay.Snapshot());
    }

    [Fact]
    public void OnlyTheMostRecentBytesFitAfterSeveralWraps()
    {
        var replay = new TerminalReplay(5);
        Assert.Empty(replay.Snapshot());
        replay.Append("1234567890"u8);
        Assert.Equal("67890"u8.ToArray(), replay.Snapshot());
        replay.Append("ab"u8);
        Assert.Equal("890ab"u8.ToArray(), replay.Snapshot());
    }

    [Theory]
    [InlineData("é")]
    [InlineData("漢")]
    [InlineData("🐱")]
    [InlineData("\u0800")]
    [InlineData("\U00100000")]
    public void ReplayNeverStartsInsideAUtf8Character(string character)
    {
        var encoded = Encoding.UTF8.GetBytes(character);
        var replay = new TerminalReplay(encoded.Length);
        replay.Append("x"u8);
        foreach (var value in encoded) replay.Append([value]);
        Assert.Equal(encoded, replay.Snapshot());
        replay.Append("y"u8);
        Assert.Equal("y"u8.ToArray(), replay.Snapshot());
    }

    [Theory]
    [InlineData("\u001b[31m")]
    [InlineData("\u001b[?25h")]
    [InlineData("\u001b(0")]
    [InlineData("\u001b7")]
    [InlineData("\u001b0")]
    [InlineData("\u001b~")]
    [InlineData("\u001b 0")]
    [InlineData("\u001b/~")]
    [InlineData("\u001b [")]
    [InlineData("\u001b/]")]
    [InlineData("\u001b[2@")]
    [InlineData("\u001b[1~")]
    [InlineData("\u001b]0;title\a")]
    [InlineData("\u001b]8;;https://example.test/\u001b\\")]
    [InlineData("\u001bPpayload\u001b\\")]
    [InlineData("\u001bXpayload\u001b\\")]
    [InlineData("\u001b^payload\u001b\\")]
    [InlineData("\u001b_payload\u001b\\")]
    [InlineData("\u009b31m")]
    [InlineData("\u009dtitle\u009c")]
    [InlineData("\u0090payload\u009c")]
    [InlineData("\u0098payload\u009c")]
    [InlineData("\u009epayload\u009c")]
    [InlineData("\u009fpayload\u009c")]
    public void ReplayStartsAfterAnEscapeSequenceWhosePrefixWasDropped(string sequence)
    {
        var replay = new TerminalReplay(Encoding.UTF8.GetByteCount(sequence) + 3);
        var output = Encoding.UTF8.GetBytes("prefix" + sequence + "tail");
        foreach (var value in output) replay.Append([value]);
        Assert.Equal("tail"u8.ToArray(), replay.Snapshot());
    }

    [Theory]
    [InlineData("\u001b]title", "\a")]
    [InlineData("\u001bPdata\a", "\u001b\\")]
    [InlineData("\u001b]title", "\u0018")]
    [InlineData("\u001b[313", "\u001a")]
    public void AnUnfinishedSequenceLargerThanTheBufferReplaysNothing(string opening, string closing)
    {
        var replay = new TerminalReplay(4);
        replay.Append(Encoding.UTF8.GetBytes(opening));
        Assert.Empty(replay.Snapshot());
        replay.Append(Encoding.UTF8.GetBytes(closing + "done"));
        Assert.Equal("done"u8.ToArray(), replay.Snapshot());
    }

    [Fact]
    public void AReplayCanEndWithTheSamePartialCharacterThatLiveOutputCompletes()
    {
        var replay = new TerminalReplay(20);
        var encoded = Encoding.UTF8.GetBytes("🐱");
        replay.Append(encoded.AsSpan(0, 2));
        var snapshot = replay.Snapshot();
        var decoder = new UTF8Encoding(false, throwOnInvalidBytes: true).GetDecoder();
        var characters = new char[2];
        Assert.Equal(0, decoder.GetChars(snapshot, 0, snapshot.Length, characters, 0, flush: false));
        Assert.Equal(2, decoder.GetChars(encoded, 2, 2, characters, 0, flush: true));
        Assert.Equal("🐱", new string(characters));
    }

    [Fact]
    public void AClientWithoutReplayWaitsForASafeLiveBoundary()
    {
        var replay = new TerminalReplay(4);
        Assert.Equal(0, replay.Append("\u001b]title too long"u8));
        Assert.Empty(replay.Snapshot());
        Assert.False(replay.IsAtBoundary);
        Assert.Equal(4, replay.Append("more"u8));
        Assert.Equal(2, replay.Append("\u001b\\OK"u8));
        Assert.True(replay.IsAtBoundary);
        Assert.Equal("OK"u8.ToArray(), replay.Snapshot());
        Assert.Equal(0, replay.Append("next"u8));
    }

    [Fact]
    public void ATruncatedCharacterCanCompleteAtTheVeryEndOfAChunk()
    {
        var replay = new TerminalReplay(1);
        var encoded = Encoding.UTF8.GetBytes("🐱");
        replay.Append(encoded.AsSpan(0, 2));
        Assert.False(replay.IsAtBoundary);
        Assert.Empty(replay.Snapshot());
        Assert.Equal(2, replay.Append(encoded.AsSpan(2)));
        Assert.True(replay.IsAtBoundary);
        Assert.Equal(0, replay.Append("A"u8));
    }

    [Fact]
    public void TinyBuffersRecoverAfterAnOversizedCharacter()
    {
        var replay = new TerminalReplay(1);
        replay.Append(Encoding.UTF8.GetBytes("🐱"));
        Assert.Empty(replay.Snapshot());
        replay.Append("A"u8);
        Assert.Equal("A"u8.ToArray(), replay.Snapshot());
        Assert.Throws<ArgumentOutOfRangeException>(() => new TerminalReplay(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TerminalReplay(-1));
    }

    [Theory]
    [InlineData("\u001b]long\\title", "\a")]
    [InlineData("\u001b]long\u001bX\\title", "\a")]
    [InlineData("\u001b]long\u001bÜ\\title", "\a")]
    [InlineData("\u001bPlong\u001b漢\\title", "\u001b\\")]
    [InlineData("\u009dlong title", "\a")]
    [InlineData("\u0090long\atitle", "\u009c")]
    [InlineData("\u001b[31\u007f", "m")]
    [InlineData("\u001b(\u007f", "0")]
    [InlineData("\u001b\u0010", "7")]
    public void PayloadAndNonTerminatingControlsCannotCreateAnEarlyReplayBoundary(string opening, string closing)
    {
        var replay = new TerminalReplay(1);
        foreach (var value in Encoding.UTF8.GetBytes(opening)) replay.Append([value]);
        Assert.False(replay.IsAtBoundary);
        Assert.Empty(replay.Snapshot());
        replay.Append(Encoding.UTF8.GetBytes(closing));
        Assert.True(replay.IsAtBoundary);
        replay.Append("x"u8);
        Assert.Equal("x"u8.ToArray(), replay.Snapshot());
    }

    [Theory]
    [InlineData("\u109d")]
    [InlineData("\u031b")]
    [InlineData("\U0001009b")]
    public void UnicodeWhoseLowByteLooksLikeAControlRemainsText(string text)
    {
        var replay = new TerminalReplay(1);
        replay.Append(Encoding.UTF8.GetBytes(text));
        Assert.True(replay.IsAtBoundary);
        replay.Append("x"u8);
        Assert.Equal("x"u8.ToArray(), replay.Snapshot());
    }

    [Fact]
    public void InvalidUtf8PrefixesRecoverAtTheNextAsciiCharacter()
    {
        var replay = new TerminalReplay(1);
        replay.Append([0xF0, 0x9F]);
        Assert.False(replay.IsAtBoundary);
        replay.Append("x"u8);
        Assert.True(replay.IsAtBoundary);
        Assert.Empty(replay.Snapshot());
        replay.Append("y"u8);
        Assert.Equal("y"u8.ToArray(), replay.Snapshot());
    }

    [Fact]
    public void EightBitControlStringsAndCancellationHaveTheSameBoundariesAsEscapes()
    {
        var replay = new TerminalReplay(1);
        replay.Append([0x9D, (byte)'x', (byte)'y']);
        Assert.False(replay.IsAtBoundary);
        Assert.Empty(replay.Snapshot());
        replay.Append([7]);
        Assert.True(replay.IsAtBoundary);
        replay.Append([0x90, 7]);
        Assert.False(replay.IsAtBoundary);
        replay.Append([0x18]);
        Assert.True(replay.IsAtBoundary);
        replay.Append([0x9B, (byte)'3', 0x1A]);
        Assert.True(replay.IsAtBoundary);
    }
}
