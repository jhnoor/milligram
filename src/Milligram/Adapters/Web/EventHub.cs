using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;
using Milligram.Application;

namespace Milligram.Adapters.Web;

/// <summary>Server-sent events to every open viewer tab.</summary>
public sealed class EventHub : IViewerEvents
{
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(20);
    private readonly ConcurrentDictionary<Guid, Channel<string>> clients = new();

    public int Clients => clients.Count;

    public void Publish(string type, object? payload = null)
    {
        var json = JsonSerializer.Serialize(new { type, payload }, MilligramJson.Compact);
        foreach (var client in clients.Values) client.Writer.TryWrite(json);
    }

    public async Task StreamAsync(HttpContext context)
    {
        context.Response.Headers.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });
        clients[id] = channel;
        var cancellation = context.RequestAborted;
        try
        {
            await context.Response.WriteAsync("retry: 2000\n\n", cancellation);
            await context.Response.Body.FlushAsync(cancellation);
            while (!cancellation.IsCancellationRequested)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                wait.CancelAfter(Heartbeat);
                string frame;
                try
                {
                    frame = await channel.Reader.ReadAsync(wait.Token) is var json ? $"data: {json}\n\n" : "";
                }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                {
                    frame = ": ping\n\n";
                }
                await context.Response.WriteAsync(frame, cancellation);
                await context.Response.Body.FlushAsync(cancellation);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            clients.TryRemove(id, out _);
        }
    }
}
