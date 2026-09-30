using Milligram.Adapters.Companion;
using Milligram.Application;

namespace Milligram.Browser.Integration;

internal sealed class FixtureCompanion : ICompanion, IAsyncDisposable
{
    private readonly string endpoint = "milligram-browser-" + Guid.NewGuid().ToString("N");
    private readonly HostHello greeting = new(HostProtocol.Version, "browser-fixture");
    private AgentSession? session;
    private AgentPipeServer? server;
    private Task<int>? running;
    public FixtureTerminal Terminal { get; private set; } = null!;
    public int Starts { get; private set; }
    public string SessionName => "browser-fixture";
    public string AttachCommand => "milligram agent attach";
    public bool IsAvailable(out string reason) { reason = ""; return true; }
    public bool IsRunning() => running is { IsCompleted: false } && !Terminal.Exited.IsCompleted;
    public bool OpenTerminal() => false;
    public Task<AgentPipeClient> Connect(CancellationToken cancellation) => AgentPipeClient.ConnectAsync(endpoint, greeting, cancellation);

    public async Task<bool> StartAsync(CancellationToken cancellation)
    {
        if (IsRunning()) return false;
        if (running is not null) await running.WaitAsync(cancellation);
        session?.Dispose();
        server?.Dispose();
        Terminal = new FixtureTerminal();
        session = new AgentSession(Terminal, new(80, 24));
        server = new AgentPipeServer(session, endpoint, greeting, Console.Error.WriteLine);
        running = new AgentHostRuntime(Terminal, session, server, Console.Error.WriteLine).RunAsync(() => { }, CancellationToken.None);
        await server.Ready.WaitAsync(cancellation);
        await Terminal.Produce($"\u001b[?2004h\u001b[32mREADY {++Starts}: Grüße 漢字 🐱\u001b[0m\r\nOrder.cs:2\r\n$ ");
        return true;
    }

    public void Stop() { if (running is not null) Terminal.Stop(); }
    public void Ring() => RingAsync().GetAwaiter().GetResult();
    private async Task RingAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var client = await Connect(timeout.Token);
        await client.SendAsync(new(HostFrameKind.Ring, []), timeout.Token);
        await client.SendAsync(new(HostFrameKind.Status, []), timeout.Token);
        while (await client.ReadAsync(timeout.Token) is { } frame)
            if (frame.Kind == HostFrameKind.Status) return;
        throw new IOException("The fixture host closed before ringing.");
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        if (running is not null && await running.WaitAsync(TimeSpan.FromSeconds(15)) != 0)
            throw new IOException("The fixture host did not shut down cleanly.");
        session?.Dispose();
        server?.Dispose();
    }
}
