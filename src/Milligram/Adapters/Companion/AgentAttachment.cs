using System.Threading.Channels;
using Milligram.Application;

namespace Milligram.Adapters.Companion;

/// <summary>Attaches one local console without taking ownership of the agent or leaving a console reader behind.</summary>
public sealed class AgentAttachment(Func<CancellationToken, Task<AgentPipeClient>> connect, Func<ILocalTerminal> open)
{
    public async Task<int> RunAsync(CancellationToken cancellation)
    {
        try
        {
            using var greeting = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            greeting.CancelAfter(TimeSpan.FromSeconds(3));
            await using var client = await connect(greeting.Token);
            using var terminal = open();
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            var changed = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
            void Resize() => changed.Writer.TryWrite(true);
            void Interrupt() => lifetime.Cancel();
            terminal.Resized += Resize;
            terminal.Interrupted += Interrupt;
            Resize();
            var input = Task.Run(Input);
            var output = Output();
            var resizing = Resizing();
            try
            {
                var finished = await Task.WhenAny(input, output, resizing);
                try { return await finished; }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return 0; }
            }
            finally
            {
                terminal.Resized -= Resize;
                terminal.Interrupted -= Interrupt;
                await lifetime.CancelAsync();
                await client.DisposeAsync();
                foreach (var task in new[] { input, output, resizing })
                    try { await task; }
                    catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException) { }
            }

            async Task<int> Input()
            {
                var keys = new TerminalDetach();
                var buffer = new byte[HostProtocol.MaxPayload - 1];
                while (true)
                {
                    var count = terminal.Read(buffer, lifetime.Token);
                    if (count == 0) return 0;
                    var bytes = keys.Feed(buffer.AsSpan(0, count), out var detached);
                    if (bytes.Length > 0) await client.SendAsync(new HostFrame(HostFrameKind.Input, bytes), lifetime.Token);
                    if (detached) return 0;
                }
            }

            async Task<int> Output()
            {
                while (await client.ReadAsync(lifetime.Token) is { } frame)
                {
                    if (frame.Kind == HostFrameKind.Output) terminal.Write(frame.Payload);
                    else if (frame.Kind == HostFrameKind.Exited) return HostProtocol.ReadExitCode(frame);
                    else if (frame.Kind != HostFrameKind.Status) throw new InvalidDataException("Unexpected agent terminal frame.");
                }
                throw new EndOfStreamException("The agent host disconnected.");
            }

            async Task<int> Resizing()
            {
                await foreach (var _ in changed.Reader.ReadAllAsync(lifetime.Token))
                    await client.SendAsync(HostProtocol.Resize(terminal.Size), lifetime.Token);
                return 0;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return 0; }
        catch (Exception error) when (error is IOException or InvalidDataException or OperationCanceledException)
        {
            throw new MilligramException("Could not attach to the agent. Run `milligram agent start` and try again: " + error.Message);
        }
    }
}
