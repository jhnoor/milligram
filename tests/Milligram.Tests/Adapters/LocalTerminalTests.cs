using Milligram.Adapters.Companion;
using Milligram.Adapters.Processes;

namespace Milligram.Tests.Adapters;

public class LocalTerminalTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CleanupRestoresModesOnceEvenIfTheTerminalRejectsScreenReset(bool unexpected)
    {
        var terminal = new Terminal(unexpected);
        if (unexpected) Assert.Throws<InvalidOperationException>(terminal.Dispose);
        else terminal.Dispose();
        terminal.Dispose();
        Assert.Equal(1, terminal.Restores);
    }

    private sealed class Terminal(bool unexpected) : LocalTerminal
    {
        public int Restores { get; private set; }
        public override TerminalSize Size => new(80, 24);
        public override int Read(byte[] buffer, CancellationToken cancellation) => throw new NotSupportedException();
        public override void Write(byte[] bytes)
        {
            if (unexpected) throw new InvalidOperationException("output failed");
            throw new IOException("output closed");
        }
        protected override void Restore() => Restores++;
    }
}
