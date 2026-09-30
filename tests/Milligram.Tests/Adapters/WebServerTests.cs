using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Milligram.Adapters.Web;
using Milligram.Analysis.CSharp;
using Milligram.Application;
using Milligram.Domain.Metrics;

namespace Milligram.Tests.Adapters;

public class WebServerTests : IAsyncLifetime
{
    private readonly TempProject project = new(("milligram.json", """{ "prefix": "Shop" }"""),
        ("Order.cs", "namespace Shop; public class Order { public int Total() => 1; }"));
    private readonly EventHub events = new();
    private readonly FakeCompanion companion = new();
    private Workspace workspace = null!;
    private WebServer server = null!;
    private WebApplication app = null!;
    private HttpClient client = null!;

    public async Task InitializeAsync()
    {
        workspace = new Workspace(new ProjectPaths(project.Root), new CSharpScanner());
        workspace.Load();
        workspace.EditPolicy(p => p with { Editor = $"\"{Path.Combine(project.Root, "no-editor")}\" {{file}}:{{line}}" });
        workspace.Generate();
        var processes = new FakeProcessRunner();
        var projects = new FakeProjectLocator();
        var jobs = new JobQueue(events);
        var actions = new ViewerActions(workspace, new PolicyEditor(workspace),
            new CrapService(workspace, projects, processes, new FakeCoverageReader(new(new Dictionary<string, IReadOnlyDictionary<int, int>>()))),
            new MutationService(workspace, projects, processes, new FakeMutationReader()), jobs, companion, events);
        using var port = new TcpListener(IPAddress.Loopback, 0);
        port.Start();
        var preferred = Math.Min(((IPEndPoint)port.LocalEndpoint).Port, 65000);
        port.Stop();
        server = new WebServer(workspace, actions, jobs, companion, events);
        var started = await server.StartAsync(preferred, CancellationToken.None);
        app = started.App;
        client = new HttpClient { BaseAddress = new Uri(started.Url.Replace("localhost", "127.0.0.1", StringComparison.Ordinal)), Timeout = TimeSpan.FromSeconds(5) };
    }

    public async Task DisposeAsync()
    {
        client?.Dispose();
        if (app is not null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await app.StopAsync(timeout.Token);
            await app.DisposeAsync();
        }
        project.Dispose();
    }

    [Fact]
    public async Task ServesEmbeddedAssetsAndSourceWithTheSameJsonContractAsTheViewer()
    {
        foreach (var path in new[] { "", "app.js", "style.css", "lib/elk.bundled.js", "agent.html", "agent.js", "agent-panel.js", "agent.css", "api.js", "terminal-links.js", "lib/xterm/xterm.mjs", "lib/xterm/addon-fit.mjs", "lib/xterm/xterm.css" })
        {
            using var asset = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
            Assert.True(asset.Headers.CacheControl!.NoCache);
            Assert.Equal("frame-ancestors 'none'", Assert.Single(asset.Headers.GetValues("Content-Security-Policy")));
            Assert.Equal("nosniff", Assert.Single(asset.Headers.GetValues("X-Content-Type-Options")));
            Assert.NotEmpty(await asset.Content.ReadAsByteArrayAsync());
        }
        using var meta = JsonDocument.Parse(await client.GetStringAsync("api/meta"));
        Assert.Equal(1, meta.RootElement.GetProperty("types").GetInt32());
        Assert.False(meta.RootElement.GetProperty("agent").TryGetProperty("reason", out _));
        var terminal = meta.RootElement.GetProperty("agent").GetProperty("terminal");
        Assert.False(terminal.GetProperty("available").GetBoolean());
        Assert.False(terminal.TryGetProperty("protocol", out _));
        using var card = JsonDocument.Parse(await client.GetStringAsync("api/type?id=Shop.Order"));
        Assert.Equal("Order", card.RootElement.GetProperty("name").GetString());
        Assert.Equal("method", card.RootElement.GetProperty("members")[0].GetProperty("kind").GetString());
        using var source = JsonDocument.Parse(await client.GetStringAsync("api/source?file=Order.cs"));
        Assert.Contains("Total()", source.RootElement.GetProperty("text").GetString());
    }

    [Theory]
    [InlineData("auto", true)]
    [InlineData("none", false)]
    [InlineData("custom-terminal", false)]
    public async Task TerminalMetadataRespectsTheAutomaticPanelPreference(string preference, bool opens)
    {
        workspace.EditPolicy(p => p with { Agent = p.Agent with { Terminal = preference } });
        using var meta = JsonDocument.Parse(await client.GetStringAsync("api/meta"));
        Assert.Equal(opens, meta.RootElement.GetProperty("agent").GetProperty("terminal").GetProperty("autoOpen").GetBoolean());
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("Cached project title", "Cached project title")]
    public async Task MetadataKeepsTheCachedModelTitleOrFallsBackToTheProjectDirectory(string title, string? expected)
    {
        JsonFile.Write(workspace.Paths.ModelFile, workspace.Model with { Title = title });
        workspace.ReloadModel();
        using var meta = JsonDocument.Parse(await client.GetStringAsync("api/meta"));
        Assert.Equal(expected ?? workspace.DefaultTitle, meta.RootElement.GetProperty("title").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    [InlineData("1,1")]
    public async Task PostsNeedTheExactViewerHeader(string? header)
    {
        using var response = await Post("api/action", """{ "op": "new-proposal" }""", header);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(workspace.Policy.Proposals);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    public async Task LoopbackHostNamesAreAcceptedOnlyAtTheBoundPort(string host)
    {
        using var valid = new HttpRequestMessage(HttpMethod.Get, "api/meta");
        valid.Headers.Host = host + ":" + client.BaseAddress!.Port;
        using var accepted = await client.SendAsync(valid);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        using var wrongPort = new HttpRequestMessage(HttpMethod.Get, "api/meta");
        wrongPort.Headers.Host = host;
        using var rejected = await client.SendAsync(wrongPort);
        Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
    }

    [Fact]
    public async Task ValidPostsReturnTheirResultAndHostSpoofingCannotReadTheProject()
    {
        using var response = await Post("api/action", """{ "op": "new-proposal", "name": "Try it" }""");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(result.RootElement.GetProperty("ok").GetBoolean());
        Assert.Single(workspace.Policy.Proposals);
        using var meta = JsonDocument.Parse(await client.GetStringAsync("api/meta"));
        var contexts = meta.RootElement.GetProperty("contexts");
        Assert.Equal("real", contexts[0].GetProperty("id").GetString());
        Assert.Equal("Real diagram", contexts[0].GetProperty("name").GetString());
        Assert.False(contexts[0].GetProperty("isProposal").GetBoolean());
        Assert.True(contexts[1].GetProperty("isProposal").GetBoolean());

        using var spoofed = new HttpRequestMessage(HttpMethod.Get, "api/source?file=Order.cs");
        spoofed.Headers.Host = "example.com:" + client.BaseAddress!.Port;
        using var denied = await client.SendAsync(spoofed);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Theory]
    [InlineData("api/action", "{")]
    [InlineData("api/open", "{")]
    [InlineData("api/action", "null")]
    [InlineData("api/open", "null")]
    public async Task MalformedRequestsAreClientErrorsAndDoNotBreakTheViewer(string path, string body)
    {
        using var response = await Post(path, body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var healthy = await client.GetAsync("api/meta");
        Assert.Equal(HttpStatusCode.OK, healthy.StatusCode);
    }

    [Fact]
    public async Task InvalidOrOutsidePathsCannotBeReadOrOpened()
    {
        using var outside = new TempProject(("Outside.cs", "outside"));
        var absolute = Path.Combine(outside.Root, "Outside.cs");
        foreach (var file in new[] { absolute, Path.GetRelativePath(project.Root, absolute), "missing.cs", "\0" })
        {
            using var response = await client.GetAsync("api/source?file=" + Uri.EscapeDataString(file));
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            using var open = await Post("api/open", JsonSerializer.Serialize(new { file, line = 1 }));
            Assert.Equal(HttpStatusCode.NotFound, open.StatusCode);
        }
        using var absent = await Post("api/open", "{}");
        Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        using var unknown = await client.GetAsync("api/type?id=Shop.Missing");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task OversizedSourceIsRejectedBeforeItIsRead()
    {
        var path = project.Write("Large.cs", "");
        using (var file = File.OpenWrite(path)) file.SetLength(4 * 1024 * 1024 + 1);
        using var response = await client.GetAsync("api/source?file=Large.cs");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        File.WriteAllText(path, new string(' ', 4 * 1024 * 1024));
        using var boundary = await client.GetAsync("api/source?file=Large.cs");
        Assert.Equal(HttpStatusCode.OK, boundary.StatusCode);
    }

    [Fact]
    public async Task EditorStartupFailureIsReportedWithoutLosingTheResponse()
    {
        using var response = await Post("api/open", """{ "file": "Order.cs", "line": 1 }""");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(result.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("Could not start the editor", result.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task BusyPortsFallForwardAndTheNewPortStillPassesTheGuard()
    {
        var original = client.BaseAddress!.Port;
        var next = await server.StartAsync(original, CancellationToken.None);
        try
        {
            var address = new Uri(next.Url.Replace("localhost", "127.0.0.1", StringComparison.Ordinal));
            Assert.InRange(address.Port, original + 1, original + 29);
            using var other = new HttpClient();
            using var response = await other.GetAsync(new Uri(address, "api/meta"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            await next.App.StopAsync();
            await next.App.DisposeAsync();
        }
    }

    [Fact]
    public async Task MetadataShowsMeasuredTimesAndUnavailableAgentsAccurately()
    {
        using var empty = JsonDocument.Parse(await client.GetStringAsync("api/meta"));
        Assert.False(empty.RootElement.GetProperty("metrics").TryGetProperty("crapAt", out _));
        Assert.False(empty.RootElement.GetProperty("metrics").TryGetProperty("mutationAt", out _));
        var measuredAt = DateTimeOffset.UnixEpoch.AddDays(1);
        var member = workspace.Model.Types[0].Members[0];
        workspace.SaveMetrics(new CrapSnapshot(measuredAt, new Dictionary<string, CrapEntry> { [member.Id] = new(1, 1, 1, 1, 1, member.Hash) }));
        workspace.SaveMetrics(new MutationSnapshot(measuredAt, new Dictionary<string, MutationEntry>(),
            new Dictionary<string, DateTimeOffset> { ["Order.cs"] = measuredAt }));
        workspace.EditPolicy(p => p with { Title = "Custom title" });
        companion.Available = false;
        companion.Running = true;

        using var meta = JsonDocument.Parse(await client.GetStringAsync("api/meta"));
        Assert.Equal("Custom title", meta.RootElement.GetProperty("title").GetString());
        Assert.Equal(measuredAt, meta.RootElement.GetProperty("metrics").GetProperty("crapAt").GetDateTimeOffset());
        Assert.Equal(measuredAt, meta.RootElement.GetProperty("metrics").GetProperty("mutationAt").GetDateTimeOffset());
        var agent = meta.RootElement.GetProperty("agent");
        Assert.False(agent.GetProperty("available").GetBoolean());
        Assert.True(agent.GetProperty("running").GetBoolean());
        Assert.Equal("tmux is not installed.", agent.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task AnIncompatibleAgentDoesNotPreventTheViewerFromReadingItsMetadata()
    {
        companion.RunningError = "Agent host uses protocol 2; restart it with a matching Milligram version.";
        using var meta = JsonDocument.Parse(await client.GetStringAsync("api/meta"));
        var agent = meta.RootElement.GetProperty("agent");
        Assert.False(agent.GetProperty("available").GetBoolean());
        Assert.False(agent.GetProperty("running").GetBoolean());
        Assert.Equal(companion.RunningError, agent.GetProperty("reason").GetString());
        using var view = await client.GetAsync("api/view");
        Assert.Equal(HttpStatusCode.OK, view.StatusCode);
    }

    [Fact]
    public async Task LiveEventsReachAnAttachedViewer()
    {
        using var response = await client.GetAsync("api/events", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.Equal("retry: 2000", await reader.ReadLineAsync(timeout.Token));
        Assert.Equal("", await reader.ReadLineAsync(timeout.Token));
        events.Publish("reload", new { version = 2 });
        var frame = await reader.ReadLineAsync(timeout.Token);
        Assert.StartsWith("data: ", frame);
        using var data = JsonDocument.Parse(frame![6..]);
        Assert.Equal("reload", data.RootElement.GetProperty("type").GetString());
        Assert.Equal(2, data.RootElement.GetProperty("payload").GetProperty("version").GetInt32());
    }

    private async Task<HttpResponseMessage> Post(string path, string body, string? header = "1")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (header is not null) request.Headers.Add("X-Milligram", header);
        return await client.SendAsync(request);
    }
}
