using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Milligram.Adapters.Companion;
using Milligram.Application;

namespace Milligram.Adapters.Web;

public sealed record TerminalAccess(bool Available, string? Protocol);

/// <summary>Authenticates browser attachments before opening a host pipe; tokens belong to one viewer run.</summary>
public sealed class AgentWebSocket(int port, Func<bool> enabled, Func<CancellationToken, Task<AgentPipeClient>>? connect)
{
    private readonly string protocol = "milligram-terminal.v1." + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    public TerminalAccess Access => enabled() && connect is not null ? new(true, protocol) : new(false, null);

    public async Task HandleAsync(HttpContext context, CancellationToken stopping)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!Access.Available)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        var origins = context.Request.Headers.Origin;
        var protocols = context.WebSockets.WebSocketRequestedProtocols;
        if (origins.Count != 1 || !TrustedOrigin(origins[0]) || protocols.Count != 1 ||
            !string.Equals(protocols[0], protocol, StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stopping);
        await using var pipe = await ConnectAsync(lifetime.Token);
        if (pipe is null)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }
        using var socket = await context.WebSockets.AcceptWebSocketAsync(protocol);
        await AgentWebRelay.RunAsync(socket, pipe, lifetime.Token);
    }

    private async Task<AgentPipeClient?> ConnectAsync(CancellationToken cancellation)
    {
        try
        {
            using var greeting = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            greeting.CancelAfter(TimeSpan.FromSeconds(3));
            return await connect!(greeting.Token);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or MilligramException or OperationCanceledException)
        {
            return null;
        }
    }

    private bool TrustedOrigin(string? origin) =>
        string.Equals(origin, new Uri($"http://localhost:{port}").GetLeftPart(UriPartial.Authority), StringComparison.Ordinal) ||
        string.Equals(origin, new Uri($"http://127.0.0.1:{port}").GetLeftPart(UriPartial.Authority), StringComparison.Ordinal);
}
