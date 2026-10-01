using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Milligram.Adapters.Processes;
using Milligram.Adapters.Web;
using Milligram.Analysis.Coverage;
using Milligram.Analysis.CSharp;
using Milligram.Analysis.DotNet;
using Milligram.Analysis.Mutation;
using Milligram.Application;

namespace Milligram.Browser.Integration;

internal sealed class ViewerFixture : IAsyncDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "milligram-browser-" + Guid.NewGuid().ToString("N"));
    private WebApplication? app;
    private readonly EventHub events = new();
    public FixtureCompanion Companion { get; } = new();
    public Workspace Workspace { get; private set; } = null!;
    public string Url { get; private set; } = "";
    public int Viewers => events.Clients;

    public async Task Start(bool diagram = false, string? cachedProject = null)
    {
        Directory.CreateDirectory(root);
        if (diagram)
        {
            await File.WriteAllTextAsync(Path.Combine(root, "milligram.json"), """{"prefix":"","levels":[["Global000"],["Wide"]],"agent":{"enabled":false}}""");
            var source = string.Join('\n', Enumerable.Range(0, 500).Select(i => $"public class Global{i:D3} {{ public Wide.N{i % 120:D3}.Item Next; public int Value() => {i}; }}")
                .Concat(Enumerable.Range(0, 120).Select(i => $"namespace Wide.N{i:D3} {{ public class Item {{ public Global{i:D3} Back; }} }}")));
            await File.WriteAllTextAsync(Path.Combine(root, "Wide.cs"), source);
        }
        else
        {
            await File.WriteAllTextAsync(Path.Combine(root, "milligram.json"), """{"prefix":"Shop","agent":{"host":"milligram"}}""");
            await File.WriteAllTextAsync(Path.Combine(root, "Order.cs"), "namespace Shop;\npublic class Order { public int Total() => 1; }\n");
        }
        Workspace = new Workspace(new ProjectPaths(cachedProject ?? root), new CSharpScanner());
        Workspace.Load();
        if (!diagram && cachedProject is null) Workspace.Generate();
        var jobs = new JobQueue(events);
        var locator = new DotNetProjectLocator();
        var processes = new ProcessRunner();
        var actions = new ViewerActions(Workspace, new PolicyEditor(Workspace),
            new CrapService(Workspace, locator, processes, new CoberturaReader()),
            new MutationService(Workspace, locator, processes, new StrykerReportReader()), jobs, Companion, events);
        if (!diagram) await Companion.StartAsync(CancellationToken.None);
        using var port = new TcpListener(IPAddress.Loopback, 0);
        port.Start();
        var preferred = Math.Min(((IPEndPoint)port.LocalEndpoint).Port, 65000);
        port.Stop();
        (app, Url) = await new WebServer(Workspace, actions, jobs, Companion, events, () => true, Companion.Connect)
            .StartAsync(preferred, CancellationToken.None);
    }

    public void GenerateDiagram()
    {
        Workspace.Generate();
        events.Publish("model", new { });
    }

    public async ValueTask DisposeAsync()
    {
        if (app is not null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await app.StopAsync(timeout.Token);
            await app.DisposeAsync();
        }
        await Companion.DisposeAsync();
        Directory.Delete(root, recursive: true);
    }
}
