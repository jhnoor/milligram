using System.IO.Pipelines;
using System.Text;
using Milligram.Adapters.Companion;

namespace Milligram.Browser.Integration;

/// <summary>A deterministic terminal peer; browser tests use the production session, pipe and WebSocket transport.</summary>
internal sealed class FixtureTerminal : IAgentTerminal
{
    private readonly Pipe input = new();
    private readonly Pipe output = new();
    private readonly SemaphoreSlim writer = new(1, 1);
    private readonly TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly StringBuilder received = new();
    private readonly Lock gate = new();
    private readonly Task echo;
    private int stopped;

    public FixtureTerminal()
    {
        Input = input.Writer.AsStream();
        Output = output.Reader.AsStream();
        echo = Echo();
    }

    public int Pid => Environment.ProcessId;
    public Stream Input { get; }
    public Stream Output { get; }
    public Task<int> Exited => exited.Task;
    public TerminalSize Size { get; private set; } = new(80, 24);
    public string Received { get { lock (gate) return received.ToString(); } }
    public void Resize(TerminalSize size) => Size = size;

    public async Task Produce(string text)
    {
        await writer.WaitAsync();
        try { await output.Writer.WriteAsync(Encoding.UTF8.GetBytes(text)); }
        finally { writer.Release(); }
    }

    private async Task Echo()
    {
        using var reader = new StreamReader(input.Reader.AsStream(), Encoding.UTF8);
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer) is var count && count > 0)
        {
            var text = new string(buffer, 0, count);
            lock (gate) received.Append(text);
            await Produce(text.Replace("\u0003", "^C", StringComparison.Ordinal));
        }
        await writer.WaitAsync();
        try { await output.Writer.CompleteAsync(); }
        finally { writer.Release(); }
    }

    public void Stop()
    {
        if (Interlocked.Exchange(ref stopped, 1) != 0) return;
        input.Writer.Complete();
        exited.TrySetResult(0);
    }

    public void Dispose()
    {
        Stop();
        echo.GetAwaiter().GetResult();
        Input.Dispose();
        Output.Dispose();
    }
}
