using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Milligram.Adapters.Companion;
using Milligram.Adapters.Web;
using Milligram.Analysis.CSharp;
using Milligram.Application;

namespace Milligram.Tests.Adapters;

public class AgentWebSocketTests : IAsyncLifetime
{
    private readonly TempProject project = new();
    private readonly CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(15));
    private readonly Channel<NamedPipeServerStream> connections = Channel.CreateUnbounded<NamedPipeServerStream>();
    private readonly List<NamedPipeServerStream> pipes = [];
    private WebServer server = null!;
    private WebApplication app = null!;
    private HttpClient client = null!;
    private string protocol = "";
    private bool enabled = true;
    private int attempts;
    private Func<CancellationToken, Task<AgentPipeClient>>? fail;
    private CancellationToken Token => lifetime.Token;

    public async Task InitializeAsync()
    {
        var workspace = new Workspace(new ProjectPaths(project.Root), new CSharpScanner());
        workspace.Load();
        var events = new EventHub();
        var jobs = new JobQueue(events);
        var companion = new FakeCompanion();
        var projects = new FakeProjectLocator();
        var processes = new FakeProcessRunner();
        var actions = new ViewerActions(workspace, new PolicyEditor(workspace),
            new CrapService(workspace, projects, processes, new FakeCoverageReader(new(new Dictionary<string, IReadOnlyDictionary<int, int>>()))),
            new MutationService(workspace, projects, processes, new FakeMutationReader()), jobs, companion, events);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = Math.Min(((IPEndPoint)listener.LocalEndpoint).Port, 65000);
        listener.Stop();
        server = new WebServer(workspace, actions, jobs, companion, events, () => enabled, ConnectHost);
        var started = await server.StartAsync(port, Token);
        app = started.App;
        client = new HttpClient { BaseAddress = new Uri(started.Url.Replace("localhost", "127.0.0.1", StringComparison.Ordinal)), Timeout = TimeSpan.FromSeconds(5) };
        protocol = await ReadProtocol(client);
    }

    public async Task DisposeAsync()
    {
        lifetime.Cancel();
        if (app is not null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await app.StopAsync(timeout.Token);
            await app.DisposeAsync();
        }
        foreach (var pipe in pipes) await pipe.DisposeAsync();
        client?.Dispose();
        project.Dispose();
        lifetime.Dispose();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("https://localhost:{port}")]
    [InlineData("http://localhost:1")]
    [InlineData("http://localhost:{port}/")]
    [InlineData("http://LOCALHOST:{port}")]
    [InlineData("http://evil.example")]
    [InlineData("http://localhost:{port}.evil.example")]
    [InlineData("http://localhost:{port}, http://evil.example")]
    public async Task ATokenCannotBypassAnUntrustedOrMissingOrigin(string? origin)
    {
        using var request = Upgrade(origin?.Replace("{port}", client.BaseAddress!.Port.ToString(), StringComparison.Ordinal), protocol);
        using var response = await client.SendAsync(request, Token);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, attempts);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong")]
    [InlineData("duplicate")]
    [InlineData("extra")]
    [InlineData("case")]
    public async Task TheExactSingleSubprotocolIsRequired(string? token)
    {
        token = token switch { "duplicate" => protocol + ", " + protocol, "extra" => protocol + ", extra", "case" => protocol.ToUpperInvariant(), _ => token };
        using var request = Upgrade(client.BaseAddress!.GetLeftPart(UriPartial.Authority), token);
        using var response = await client.SendAsync(request, Token);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, attempts);
    }

    [Theory]
    [InlineData("evil.example")]
    [InlineData("localhost")]
    [InlineData("localhost:1")]
    public async Task AValidOriginAndTokenCannotBypassTheHostGuard(string host)
    {
        using var request = Upgrade(client.BaseAddress!.GetLeftPart(UriPartial.Authority), protocol);
        request.Headers.Host = host;
        using var response = await client.SendAsync(request, Token);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, attempts);
    }

    [Fact]
    public async Task PlainHttpAndUnavailableTerminalsNeverConnectToTheHost()
    {
        using (var request = Upgrade(client.BaseAddress!.GetLeftPart(UriPartial.Authority), protocol))
        {
            request.Headers.Remove("Upgrade");
            using var response = await client.SendAsync(request, Token);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        enabled = false;
        using var disabled = await client.GetAsync("api/agent/terminal", Token);
        Assert.Equal(HttpStatusCode.NotFound, disabled.StatusCode);
        Assert.True(disabled.Headers.CacheControl!.NoStore);
        using var meta = JsonDocument.Parse(await client.GetStringAsync("api/meta", Token));
        var terminal = meta.RootElement.GetProperty("agent").GetProperty("terminal");
        Assert.False(terminal.GetProperty("available").GetBoolean());
        Assert.False(terminal.TryGetProperty("protocol", out _));
        Assert.Equal(0, attempts);
    }

    [Fact]
    public async Task TokensAreRandomPerServerAndMetadataCannotBeCachedOrReadThroughCors()
    {
        Assert.StartsWith("milligram-terminal.v1.", protocol);
        Assert.Equal(32, Convert.FromHexString(protocol["milligram-terminal.v1.".Length..]).Length);
        Assert.Equal(protocol, await ReadProtocol(client));
        using var crossSite = new HttpRequestMessage(HttpMethod.Get, "api/meta");
        crossSite.Headers.Add("Origin", "https://evil.example");
        using var response = await client.SendAsync(crossSite, Token);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        using var preflight = new HttpRequestMessage(HttpMethod.Options, "api/meta");
        preflight.Headers.Add("Origin", "https://evil.example");
        preflight.Headers.Add("Access-Control-Request-Method", "GET");
        using var options = await client.SendAsync(preflight, Token);
        Assert.False(options.Headers.Contains("Access-Control-Allow-Origin"));
        var other = await server.StartAsync(client.BaseAddress!.Port, Token);
        try
        {
            using var second = new HttpClient { BaseAddress = new Uri(other.Url.Replace("localhost", "127.0.0.1", StringComparison.Ordinal)) };
            Assert.NotEqual(protocol, await ReadProtocol(second));
            using var wrongServer = Upgrade(second.BaseAddress!.GetLeftPart(UriPartial.Authority), protocol);
            using var denied = await second.SendAsync(wrongServer, Token);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            Assert.Equal(0, attempts);
        }
        finally { await other.App.StopAsync(Token); await other.App.DisposeAsync(); }
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    public async Task ValidClientsRelayBinaryInputAndOutputAndJsonControls(string originHost)
    {
        using var socket = await Attach(originHost);
        var pipe = await connections.Reader.ReadAsync(Token);
        Assert.Equal(protocol, socket.SubProtocol);
        var bytes = Encoding.UTF8.GetBytes("before 漢 🐱\u001b[31m");
        await HostProtocol.WriteAsync(pipe, new HostFrame(HostFrameKind.Output, bytes[..9]), Token);
        await HostProtocol.WriteAsync(pipe, new HostFrame(HostFrameKind.Output, bytes[9..]), Token);
        var first = await Read(socket);
        var second = await Read(socket);
        Assert.Equal(WebSocketMessageType.Binary, first.Type);
        Assert.Equal(WebSocketMessageType.Binary, second.Type);
        Assert.Equal(bytes, first.Bytes.Concat(second.Bytes));
        await socket.SendAsync(bytes.AsMemory(0, 9), WebSocketMessageType.Binary, false, Token);
        await socket.SendAsync(bytes.AsMemory(9), WebSocketMessageType.Binary, true, Token);
        var input = (await HostProtocol.ReadAsync(pipe, Token))!;
        Assert.Equal(HostFrameKind.Input, input.Kind);
        Assert.Equal(bytes, input.Payload);
        await SendText(socket, """{"type":"resize","columns":96,"rows":31}""");
        Assert.Equal(new TerminalSize(96, 31), HostProtocol.ReadSize((await HostProtocol.ReadAsync(pipe, Token))!));
        await SendText(socket, """{"type":"status"}""");
        var status = (await HostProtocol.ReadAsync(pipe, Token))!;
        Assert.Equal(HostFrameKind.Status, status.Kind);
        Assert.Empty(status.Payload);
        await HostProtocol.WriteAsync(pipe, HostProtocol.Json(HostFrameKind.Status, new AgentHostStatus(42, 2, 96, 31)), Token);
        var message = await Read(socket);
        Assert.Equal(WebSocketMessageType.Text, message.Type);
        using var json = JsonDocument.Parse(message.Bytes);
        Assert.Equal("status", json.RootElement.GetProperty("type").GetString());
        Assert.Equal(42, json.RootElement.GetProperty("pid").GetInt32());
        Assert.Equal(2, json.RootElement.GetProperty("clients").GetInt32());
        Assert.Equal(96, json.RootElement.GetProperty("columns").GetInt32());
        Assert.Equal(31, json.RootElement.GetProperty("rows").GetInt32());
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "detach", Token);
        Assert.Equal("Detached.", socket.CloseStatusDescription);
        Assert.Null(await HostProtocol.ReadAsync(pipe, Token));
    }

    [Fact]
    public async Task DetachingOneBrowserLeavesAnotherConnectionUsable()
    {
        using var first = await Attach();
        var firstPipe = await connections.Reader.ReadAsync(Token);
        using var second = await Attach();
        var secondPipe = await connections.Reader.ReadAsync(Token);
        await first.CloseAsync(WebSocketCloseStatus.NormalClosure, "detach", Token);
        Assert.Null(await HostProtocol.ReadAsync(firstPipe, Token));
        await second.SendAsync("still here"u8.ToArray().AsMemory(), WebSocketMessageType.Binary, true, Token);
        var input = (await HostProtocol.ReadAsync(secondPipe, Token))!;
        Assert.Equal(HostFrameKind.Input, input.Kind);
        Assert.Equal("still here", Encoding.UTF8.GetString(input.Payload));
        await second.CloseAsync(WebSocketCloseStatus.NormalClosure, "detach", Token);
        Assert.Null(await HostProtocol.ReadAsync(secondPipe, Token));
    }

    [Fact]
    public async Task AgentExitFollowsTheLastOutputAndClosesCleanlyWithItsExitCode()
    {
        using var socket = await Attach();
        var pipe = await connections.Reader.ReadAsync(Token);
        await HostProtocol.WriteAsync(pipe, new HostFrame(HostFrameKind.Output, [65]), Token);
        await HostProtocol.WriteAsync(pipe, HostProtocol.Exited(17), Token);
        Assert.Equal(new byte[] { 65 }, (await Read(socket)).Bytes);
        var message = await Read(socket);
        Assert.Equal(WebSocketMessageType.Text, message.Type);
        using var json = JsonDocument.Parse(message.Bytes);
        Assert.Equal("exited", json.RootElement.GetProperty("type").GetString());
        Assert.Equal(17, json.RootElement.GetProperty("code").GetInt32());
        await Closed(socket, WebSocketCloseStatus.NormalClosure);
        Assert.Null(await HostProtocol.ReadAsync(pipe, Token));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"type\":\"stop\"}")]
    [InlineData("{\"type\":\"ring\"}")]
    [InlineData("{\"type\":\"resize\",\"columns\":1,\"rows\":24}")]
    [InlineData("{\"type\":\"resize\",\"columns\":80,\"rows\":1001}")]
    [InlineData("{\"type\":\"resize\",\"columns\":\"wide\",\"rows\":24}")]
    public async Task MalformedOrPrivilegedControlsCloseWithoutReachingTheHost(string text)
    {
        using var socket = await Attach();
        var pipe = await connections.Reader.ReadAsync(Token);
        await SendText(socket, text);
        await Closed(socket, WebSocketCloseStatus.InvalidPayloadData);
        Assert.Null(await HostProtocol.ReadAsync(pipe, Token));
    }

    [Theory]
    [InlineData(WebSocketMessageType.Binary, HostProtocol.MaxPayload)]
    [InlineData(WebSocketMessageType.Text, 512)]
    public async Task MessageLimitsIncludeAllFragments(WebSocketMessageType type, int limit)
    {
        using var socket = await Attach();
        var pipe = await connections.Reader.ReadAsync(Token);
        var bytes = Encoding.UTF8.GetBytes(type == WebSocketMessageType.Text ? "{\"type\":\"status\"}".PadRight(limit) : new string('x', limit));
        await socket.SendAsync(bytes.AsMemory(0, limit - 1), type, false, Token);
        await socket.SendAsync(bytes.AsMemory(limit - 1), type, true, Token);
        var frame = (await HostProtocol.ReadAsync(pipe, Token))!;
        Assert.Equal(type == WebSocketMessageType.Binary ? HostFrameKind.Input : HostFrameKind.Status, frame.Kind);
        if (type == WebSocketMessageType.Binary) Assert.Equal(bytes, frame.Payload);
        await socket.SendAsync(bytes.AsMemory(), type, false, Token);
        await socket.SendAsync(new byte[] { 32 }.AsMemory(), type, true, Token);
        await Closed(socket, WebSocketCloseStatus.MessageTooBig);
        Assert.Null(await HostProtocol.ReadAsync(pipe, Token));
    }

    [Theory]
    [InlineData("io")]
    [InlineData("access")]
    [InlineData("version")]
    [InlineData("malformed")]
    [InlineData("timeout")]
    public async Task AnUnavailableHostFailsBeforeTheUpgrade(string failure)
    {
        fail = async token =>
        {
            if (failure == "timeout") await Task.Delay(Timeout.Infinite, token);
            throw failure switch
            {
                "io" => new IOException(),
                "access" => new UnauthorizedAccessException(),
                "malformed" => new InvalidDataException(),
                _ => new MilligramException("incompatible host"),
            };
        };
        using var request = Upgrade(client.BaseAddress!.GetLeftPart(UriPartial.Authority), protocol);
        using var response = await client.SendAsync(request, Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, attempts);
    }

    [Theory]
    [InlineData("eof")]
    [InlineData("unexpected")]
    [InlineData("malformed")]
    public async Task AFailedHostConnectionClosesTheBrowser(string failure)
    {
        using var socket = await Attach();
        var pipe = await connections.Reader.ReadAsync(Token);
        if (failure == "eof") await pipe.DisposeAsync();
        else await HostProtocol.WriteAsync(pipe, failure == "unexpected" ? new HostFrame(HostFrameKind.Input, []) : new HostFrame(HostFrameKind.Status, [0xff]), Token);
        await Closed(socket, WebSocketCloseStatus.InternalServerError);
    }

    [Fact]
    public async Task ViewerShutdownDisconnectsIdleSocketsAndHostPipes()
    {
        using var socket = await Attach();
        var pipe = await connections.Reader.ReadAsync(Token);
        await app.StopAsync(Token).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(await HostProtocol.ReadAsync(pipe, Token));
        await Assert.ThrowsAsync<WebSocketException>(async () => await Read(socket));
    }

    private async Task<AgentPipeClient> ConnectHost(CancellationToken cancellation)
    {
        Interlocked.Increment(ref attempts);
        if (fail is not null) return await fail(cancellation);
        var endpoint = "mg-web-" + Guid.NewGuid().ToString("N")[..16];
        var pipe = new NamedPipeServerStream(endpoint, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        lock (pipes) pipes.Add(pipe);
        var connection = AgentPipeClient.ConnectAsync(endpoint, new HostHello(1, "test"), cancellation);
        await pipe.WaitForConnectionAsync(cancellation);
        Assert.Equal(HostFrameKind.Hello, (await HostProtocol.ReadAsync(pipe, cancellation))!.Kind);
        await HostProtocol.WriteAsync(pipe, HostProtocol.Json(HostFrameKind.Hello, new HostHello(1, "test")), cancellation);
        var connected = await connection;
        connections.Writer.TryWrite(pipe);
        return connected;
    }

    private static async Task<string> ReadProtocol(HttpClient http)
    {
        using var meta = JsonDocument.Parse(await http.GetStringAsync("api/meta"));
        var access = meta.RootElement.GetProperty("agent").GetProperty("terminal");
        Assert.True(access.GetProperty("available").GetBoolean());
        return access.GetProperty("protocol").GetString()!;
    }

    private HttpRequestMessage Upgrade(string? origin, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "api/agent/terminal");
        request.Headers.Add("Connection", "Upgrade");
        request.Headers.Add("Upgrade", "websocket");
        request.Headers.Add("Sec-WebSocket-Version", "13");
        request.Headers.Add("Sec-WebSocket-Key", "dGhlIHNhbXBsZSBub25jZQ==");
        if (origin is not null) request.Headers.TryAddWithoutValidation("Origin", origin);
        if (token is not null) request.Headers.TryAddWithoutValidation("Sec-WebSocket-Protocol", token);
        return request;
    }

    private async Task<ClientWebSocket> Attach(string originHost = "127.0.0.1")
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", $"http://{originHost}:{client.BaseAddress!.Port}");
        socket.Options.AddSubProtocol(protocol);
        await socket.ConnectAsync(new UriBuilder(client.BaseAddress) { Scheme = "ws", Path = "/api/agent/terminal" }.Uri, Token);
        return socket;
    }

    private async Task<(WebSocketMessageType Type, byte[] Bytes)> Read(ClientWebSocket socket)
    {
        var buffer = new byte[HostProtocol.MaxPayload + 1];
        var read = await socket.ReceiveAsync(buffer.AsMemory(), Token);
        Assert.True(read.EndOfMessage);
        return (read.MessageType, buffer[..read.Count]);
    }

    private async Task Closed(ClientWebSocket socket, WebSocketCloseStatus expected)
    {
        Assert.Equal(WebSocketMessageType.Close, (await Read(socket)).Type);
        Assert.Equal(expected, socket.CloseStatus);
        Assert.False(string.IsNullOrWhiteSpace(socket.CloseStatusDescription));
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", Token);
    }

    private async Task SendText(ClientWebSocket socket, string text) =>
        await socket.SendAsync(Encoding.UTF8.GetBytes(text).AsMemory(), WebSocketMessageType.Text, true, Token);
}
