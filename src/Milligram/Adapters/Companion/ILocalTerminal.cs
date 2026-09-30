namespace Milligram.Adapters.Companion;

/// <summary>A temporary raw console whose reads observe cancellation and whose disposal restores its modes.</summary>
public interface ILocalTerminal : IDisposable
{
    TerminalSize Size { get; }
    event Action? Resized;
    event Action? Interrupted;
    int Read(byte[] buffer, CancellationToken cancellation);
    void Write(byte[] bytes);
}
