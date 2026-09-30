using System.Text;
using Milligram.Adapters.Companion;

namespace Milligram.Tests.Adapters;

public class TerminalDetachTests
{
    [Theory]
    [InlineData("hello 漢 🐱\u0003\u0004\u001b[A", "hello 漢 🐱\u0003\u0004\u001b[A", false)]
    [InlineData("before\u001ddafter", "before", true)]
    [InlineData("\u001dD", "", true)]
    [InlineData("\u001d\u001dd", "\u001dd", false)]
    [InlineData("\u001dx", "\u001dx", false)]
    public void DetachConsumesOnlyItsKeySequenceAcrossEveryPossibleReadBoundary(string input, string expected, bool detached)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        for (var boundary = 0; boundary <= bytes.Length; boundary++)
        {
            var keys = new TerminalDetach();
            var first = keys.Feed(bytes.AsSpan(0, boundary), out var firstDetached);
            var second = firstDetached ? [] : keys.Feed(bytes.AsSpan(boundary), out _);
            Assert.Equal(Encoding.UTF8.GetBytes(expected), first.Concat(second).ToArray());
            var whole = new TerminalDetach();
            whole.Feed(bytes, out var actual);
            Assert.Equal(detached, actual);
        }
    }

    [Fact]
    public void ThePendingPrefixCannotMakeAnInputFrameExceedTheWireLimit()
    {
        var keys = new TerminalDetach();
        Assert.Empty(keys.Feed([0x1d], out _));
        var bytes = keys.Feed(Enumerable.Repeat((byte)'x', HostProtocol.MaxPayload - 1).ToArray(), out var detached);
        Assert.False(detached);
        Assert.Equal(HostProtocol.MaxPayload, bytes.Length);
        Assert.Equal(0x1d, bytes[0]);
    }
}
