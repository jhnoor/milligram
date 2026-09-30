using Milligram.Adapters.Processes;

namespace Milligram.Tests.Adapters;

public class TerminalInputModeTests
{
    [Theory]
    [InlineData("normal 漢 🐱\u001b[31mred\u001b[0m", "normal 漢 🐱\u001b[31mred\u001b[0m")]
    [InlineData("a\u001b[?9001hb", "a\u001b[?9001lb")]
    [InlineData("\u001b[?9001h\u001b[?9001h", "\u001b[?9001l\u001b[?9001l")]
    [InlineData("\u001b[?9001l\u001b[?2004h", "\u001b[?9001l\u001b[?2004h")]
    [InlineData("\u001b[?900\u001b[?9001h", "\u001b[?900\u001b[?9001l")]
    public void ExtendedWindowsInputCannotBypassDetachEvenWhenItsRequestIsSplit(string input, string expected)
    {
        for (var boundary = 0; boundary <= input.Length; boundary++)
        {
            var filter = new TerminalInputMode();
            var actual = filter.Feed(input.AsSpan(0, boundary)).Concat(filter.Feed(input.AsSpan(boundary))).ToArray();
            Assert.Equal(expected, new string(actual));
        }
    }
}
