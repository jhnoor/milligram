using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Milligram.Adapters.Companion;
using Milligram.Application;
using Milligram.Domain.Hierarchy;

namespace Milligram.Adapters.Web;

/// <summary>The local HTTP server behind the browser viewer. Binds to loopback only.</summary>
public sealed class WebServer(Workspace workspace, ViewerActions actions, JobQueue jobs, ICompanion companion, EventHub events,
    Func<bool>? terminalEnabled = null, Func<CancellationToken, Task<AgentPipeClient>>? connectTerminal = null)
{
    private const long MaxSourceBytes = 4 * 1024 * 1024;

    public async Task<(WebApplication App, string Url)> StartAsync(int preferredPort, CancellationToken cancellation)
    {
        if (preferredPort is < 1 or > 65535) throw new MilligramException("The preferred port must be from 1 to 65535.");
        var lastPort = Math.Min(65535, preferredPort + 29);
        for (var port = preferredPort; port <= lastPort; port++)
        {
            cancellation.ThrowIfCancellationRequested();
            var app = Build(port);
            try
            {
                await app.StartAsync(cancellation);
                return (app, $"http://localhost:{port}/");
            }
            catch (IOException)
            {
                await app.DisposeAsync();
                cancellation.ThrowIfCancellationRequested();
            }
            catch
            {
                await app.DisposeAsync();
                throw;
            }
        }
        throw new MilligramException($"No free port in {preferredPort}-{lastPort}.");
    }

    private WebApplication Build(int port)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(o => o.Listen(IPAddress.Loopback, port));
        var app = builder.Build();
        var terminal = new AgentWebSocket(port, terminalEnabled ?? (() => false), connectTerminal);

        app.Use((context, next) => Guard(context, next, port));
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30), KeepAliveTimeout = TimeSpan.FromSeconds(15) });
        var files = new ManifestEmbeddedFileProvider(typeof(WebServer).Assembly, "wwwroot");
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = files,
            OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "no-cache",
        });

        app.MapGet("/api/meta", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            return Json(Meta(terminal.Access));
        });
        app.MapGet("/api/agent/terminal", (HttpContext context) => terminal.HandleAsync(context, app.Lifetime.ApplicationStopping));
        app.MapGet("/api/view", (string? context, string? focus) => Json(workspace.View(context, focus)));
        app.MapGet("/api/type", (string? context, string id) => workspace.Card(context, id) is { } card ? Json(card) : Results.NotFound());
        app.MapGet("/api/source", (string file) => Source(file));
        // Typed as returning IResult: as a bare invocation, the lambda would bind to RequestDelegate and drop its result.
        app.MapPost("/api/open", async Task<IResult> (HttpContext context) =>
            await Read<OpenRequest>(context) is { } request ? Open(request) : Results.BadRequest());
        app.MapPost("/api/action", async (HttpContext context) =>
            await Read<ViewerAction>(context) is { } action
                ? Json(await actions.HandleAsync(action, context.RequestAborted))
                : Results.BadRequest());
        app.MapGet("/api/events", events.StreamAsync);
        return app;
    }

    private sealed record OpenRequest(string File, int Line);

    /// <summary>Loopback host names only (no DNS rebinding), and POSTs must carry a header no cross-site form can send.</summary>
    private static Task Guard(HttpContext context, RequestDelegate next, int port)
    {
        context.Response.Headers.ContentSecurityPolicy = "frame-ancestors 'none'";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        var host = context.Request.Host;
        var local = host.Host is "localhost" or "127.0.0.1" && (host.Port ?? 80) == port;
        var trustedPost = !HttpMethods.IsPost(context.Request.Method) || context.Request.Headers["X-Milligram"] == "1";
        if (local && trustedPost) return next(context);
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }

    private object Meta(TerminalAccess terminal)
    {
        var policy = workspace.Policy;
        var model = workspace.Model;
        var metrics = workspace.Metrics;
        var available = companion.IsAvailable(out var reason);
        var running = false;
        try { running = companion.IsRunning(); }
        catch (MilligramException error) { available = false; reason = error.Message; }
        return new
        {
            title = policy.Title ?? (model.Title.Length > 0 ? model.Title : workspace.DefaultTitle),
            root = workspace.Paths.Root,
            prefix = policy.Prefix,
            generatedAt = model.GeneratedAt,
            types = model.Types.Count,
            version = workspace.Version,
            policyError = workspace.PolicyError,
            contexts = policy.Proposals
                .Select(p => new { id = p.Id, name = p.Name, isProposal = true })
                .Prepend(new { id = DiagramTree.RealContext, name = "Real diagram", isProposal = false }),
            agent = new
            {
                available,
                reason = available ? null : reason,
                running,
                session = companion.SessionName,
                attach = companion.AttachCommand,
                pendingMail = workspace.ToAgent.Count,
                terminal = new { terminal.Available, terminal.Protocol, autoOpen = policy.Agent.Terminal == "auto" },
            },
            job = jobs.Status,
            thresholds = policy.Thresholds,
            metrics = new
            {
                crapAt = metrics.Crap.IsEmpty ? (DateTimeOffset?)null : metrics.Crap.GeneratedAt,
                mutationAt = metrics.Mutation.Files.Count == 0 ? (DateTimeOffset?)null : metrics.Mutation.GeneratedAt,
            },
        };
    }

    private IResult Source(string file)
    {
        if (Resolve(file) is not { } path) return Results.NotFound();
        if (new FileInfo(path).Length > MaxSourceBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        return Json(new { file = workspace.Paths.Relative(path), text = File.ReadAllText(path) });
    }

    private IResult Open(OpenRequest request)
    {
        if (Resolve(request.File) is not { } path) return Results.NotFound();
        var opened = Desktop.OpenEditor(workspace.Policy.Editor, path, Math.Max(1, request.Line));
        return Json(opened ? ActionResult.Done() : ActionResult.Fail("Could not start the editor. Set \"editor\" in milligram.json."));
    }

    private string? Resolve(string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return null;
        try
        {
            var path = workspace.Paths.Absolute(relative);
            return workspace.Paths.Contains(path) && File.Exists(path) ? path : null;
        }
        catch (ArgumentException) { return null; }
    }

    private static async Task<T?> Read<T>(HttpContext context)
    {
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(context.Request.Body, MilligramJson.Compact, context.RequestAborted);
        }
        catch (JsonException) { return default; }
    }

    private static IResult Json(object? value) => Results.Json(value, MilligramJson.Compact);
}
