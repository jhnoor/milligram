namespace Milligram.Adapters.Companion;

/// <summary>The terminal owned by one host; streams carry bytes and shutdown ends the owned process tree.</summary>
public interface IAgentTerminal : IDisposable
{
    int Pid { get; }
    Stream Input { get; }
    Stream Output { get; }
    Task<int> Exited { get; }
    void Resize(TerminalSize size);
    void Stop();
}
