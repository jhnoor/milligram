using System.IO.Pipes;
using Milligram.Application;

namespace Milligram.Adapters.Companion;

/// <summary>A same-user connection to one host, with one frame reader and serialized writes.</summary>
public sealed class AgentPipeClient : IAsyncDisposable
{
    private readonly Stream pipe;
    private readonly SemaphoreSlim writer = new(1);

    internal AgentPipeClient(Stream pipe) => this.pipe = pipe;

    public HostHello? Greeting { get; private init; }

    public static async Task<AgentPipeClient> ConnectAsync(string endpoint, HostHello greeting, CancellationToken cancellation)
    {
        var pipe = new NamedPipeClientStream(".", endpoint, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(cancellation);
            await HostProtocol.WriteAsync(pipe, HostProtocol.Json(HostFrameKind.Hello, greeting), cancellation);
            var response = await HostProtocol.ReadAsync(pipe, cancellation) ?? throw new EndOfStreamException("The agent host closed before greeting the client.");
            var host = HostProtocol.ReadHello(response);
            if (host.Protocol != greeting.Protocol)
                throw new MilligramException($"Agent host uses protocol {host.Protocol} (Milligram {host.Version}); restart it with a matching Milligram version.");
            return new AgentPipeClient(pipe) { Greeting = host };
        }
        catch
        {
            await pipe.DisposeAsync();
            throw;
        }
    }

    public ValueTask<HostFrame?> ReadAsync(CancellationToken cancellation) => HostProtocol.ReadAsync(pipe, cancellation);

    public async Task SendAsync(HostFrame frame, CancellationToken cancellation)
    {
        await writer.WaitAsync(cancellation);
        try { await HostProtocol.WriteAsync(pipe, frame, cancellation); }
        finally { writer.Release(); }
    }

    public ValueTask DisposeAsync() => pipe.DisposeAsync();
}
