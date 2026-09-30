using System.Text;
using Milligram.Adapters.Companion;
using Milligram.Application;
using Milligram.Domain.Policies;

namespace Milligram.Tests.Adapters;

public class AgentHostTests
{
    [Fact]
    public async Task TheOwnerPreparesTheLaunchBeforePublishingAndRemovesOnlyItsDiscovery()
    {
        using var fixture = new Fixture();
        var spawning = new TaskCompletionSource<IAgentTerminal>(TaskCreationOptions.RunContinuationsAsynchronously);
        AgentLaunch? launch = null;
        var running = fixture.Host((value, size, _) =>
        {
            Assert.Equal(1, fixture.Detaches);
            Assert.Equal(new TerminalSize(80, 24), size);
            Assert.Equal(AgentBriefing.Text, File.ReadAllText(fixture.Paths.BriefingFile));
            Assert.Contains($"Agent host {Environment.ProcessId} starting (Milligram test-version).", ReadLog(fixture.Files.LogFile));
            Assert.Null(AgentHostLease.ReadDiscovery(fixture.Files));
            launch = value;
            return spawning.Task;
        }).RunAsync(fixture.Token);
        Assert.NotNull(launch);
        Assert.Equal("fixture-agent", launch.Command);
        Assert.Equal(["--fixture"], launch.Args);
        Assert.Equal(fixture.Paths.Root, launch.WorkingDirectory);
        Assert.True(File.Exists(Path.Combine(launch.ShimDirectory, OperatingSystem.IsWindows() ? "milligram.cmd" : "milligram")));
        Assert.Null(AgentHostLease.ReadDiscovery(fixture.Files));
        spawning.SetResult(fixture.Terminal);
        await using var client = await fixture.Connect();
        var discovery = AgentHostLease.ReadDiscovery(fixture.Files);
        Assert.NotNull(discovery);
        Assert.Equal(Environment.ProcessId, discovery.Pid);
        Assert.Equal(HostProtocol.Version, discovery.Protocol);
        Assert.Equal("test-version", discovery.Version);
        Assert.Equal(fixture.Files.Endpoint, discovery.Endpoint);
        Assert.InRange(discovery.Started, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow);
        await client.SendAsync(new HostFrame(HostFrameKind.Stop, []), fixture.Token);
        Assert.Equal(17, HostProtocol.ReadExitCode((await client.ReadAsync(fixture.Token))!));
        Assert.Equal(0, await running.WaitAsync(fixture.Token));
        Assert.Null(AgentHostLease.ReadDiscovery(fixture.Files));
        using var next = new AgentHostLease(fixture.Files);
        Assert.True(fixture.Terminal.Disposed);
        var log = File.ReadAllText(fixture.Files.LogFile);
        Assert.Contains("Agent host ready; agent pid 123.", log);
        Assert.Contains("Agent exited (17).", log);
    }

    [Fact]
    public async Task ADuplicateStartDoesNotTouchTheOwnersDiscoveryOrConversation()
    {
        using var fixture = new Fixture();
        var running = fixture.Host().RunAsync(fixture.Token);
        await using var client = await fixture.Connect();
        var discovery = AgentHostLease.ReadDiscovery(fixture.Files);
        var conversation = File.ReadAllText(fixture.Paths.AgentStateFile);
        var log = ReadLog(fixture.Files.LogFile);
        var duplicate = fixture.Host((_, _, _) => throw new InvalidOperationException("must not spawn"));
        Assert.Equal(1, await duplicate.RunAsync(fixture.Token));
        Assert.Equal(1, fixture.Detaches);
        Assert.Equal(discovery, AgentHostLease.ReadDiscovery(fixture.Files));
        Assert.Equal(conversation, File.ReadAllText(fixture.Paths.AgentStateFile));
        Assert.Equal(log, ReadLog(fixture.Files.LogFile));
        Assert.False(fixture.Terminal.Disposed);
        fixture.Terminal.Finish();
        Assert.Equal(0, await running.WaitAsync(fixture.Token));
    }

    [Fact]
    public async Task AnUnownedStaleDiscoveryIsReplacedOnlyAfterSuccessfulStartup()
    {
        using var fixture = new Fixture();
        var stale = new AgentHostDiscovery(456, "stale", 99, "old", DateTimeOffset.UnixEpoch);
        JsonFile.Write(fixture.Files.DiscoveryFile, stale);
        var running = fixture.Host((_, _, _) =>
        {
            Assert.Equal(stale, AgentHostLease.ReadDiscovery(fixture.Files));
            return Task.FromResult<IAgentTerminal>(fixture.Terminal);
        }).RunAsync(fixture.Token);
        await using var client = await fixture.Connect();
        Assert.NotEqual(stale, AgentHostLease.ReadDiscovery(fixture.Files));
        fixture.Terminal.Finish();
        Assert.Equal(0, await running.WaitAsync(fixture.Token));
        Assert.Null(AgentHostLease.ReadDiscovery(fixture.Files));
    }

    [Theory]
    [InlineData("detach")]
    [InlineData("disabled")]
    [InlineData("launch")]
    [InlineData("terminal")]
    public async Task StartupFailuresAreLoggedAndReleaseOwnership(string stage)
    {
        using var fixture = new Fixture();
        var spawns = 0;
        if (stage == "disabled") fixture.Policy = fixture.Policy with { Agent = fixture.Policy.Agent with { Enabled = false } };
        if (stage == "launch") fixture.Project.Write(".milligram/run/agent.json", "{");
        var host = fixture.Host((_, _, _) =>
        {
            spawns++;
            throw new IOException("native start failed");
        }, stage == "detach" ? () => throw new IOException("detach failed") : null);
        Assert.Equal(1, await host.RunAsync(fixture.Token));
        Assert.Equal(stage == "terminal" ? 1 : 0, spawns);
        Assert.Null(AgentHostLease.ReadDiscovery(fixture.Files));
        Assert.Contains("Agent host failed:", File.ReadAllText(fixture.Files.LogFile));
        if (stage == "disabled") Assert.Contains("agent.enabled", File.ReadAllText(fixture.Files.LogFile));
        using var next = new AgentHostLease(fixture.Files);
    }

    [Fact]
    public async Task CancellationBeforeStartupNeverDetachesOrCreatesAnAgent()
    {
        using var fixture = new Fixture();
        Assert.Equal(0, await fixture.Host((_, _, _) => throw new InvalidOperationException("must not spawn"))
            .RunAsync(new CancellationToken(canceled: true)));
        Assert.Equal(0, fixture.Detaches);
        Assert.False(File.Exists(fixture.Paths.BriefingFile));
        Assert.Null(AgentHostLease.ReadDiscovery(fixture.Files));
        using var next = new AgentHostLease(fixture.Files);
    }

    [Fact]
    public async Task TerminalOutputIsReplayedToClientsButNeverWrittenToTheHostLog()
    {
        using var fixture = new Fixture();
        fixture.Terminal.OutputStream.Text = "private terminal response";
        var running = fixture.Host().RunAsync(fixture.Token);
        await using var client = await fixture.Connect(expectOutput: true);
        Assert.Equal("private terminal response", Encoding.UTF8.GetString((await client.ReadAsync(fixture.Token))!.Payload));
        fixture.Terminal.Finish();
        Assert.Equal(0, await running.WaitAsync(fixture.Token));
        Assert.DoesNotContain("private terminal response", File.ReadAllText(fixture.Files.LogFile));
    }

    [Fact]
    public async Task DiscoveryPublicationFailureStillCleansTheTerminalAndReleasesTheLease()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Files.DiscoveryFile);
        Assert.Equal(1, await fixture.Host().RunAsync(fixture.Token));
        Assert.True(fixture.Terminal.Disposed);
        using var next = new AgentHostLease(fixture.Files);
        Assert.Contains("Agent exited (17).", File.ReadAllText(fixture.Files.LogFile));
    }

    private sealed class Fixture : IDisposable
    {
        public TempProject Project { get; } = new();
        private readonly CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        public ProjectPaths Paths { get; }
        public AgentHostFiles Files { get; }
        public Policy Policy { get; set; } = new() { Agent = new AgentSettings { Command = "fixture-agent", Args = ["--fixture"] } };
        public Terminal Terminal { get; } = new();
        public int Detaches { get; private set; }
        public CancellationToken Token => deadline.Token;
        public Fixture() { Paths = new ProjectPaths(Project.Root); Files = new AgentHostFiles(Paths); }
        public AgentHost Host(Func<AgentLaunch, TerminalSize, CancellationToken, Task<IAgentTerminal>>? spawn = null, Action? detach = null) =>
            new(Paths, () => Policy, ["milligram-test"], "test-version", spawn ?? ((_, _, _) => Task.FromResult<IAgentTerminal>(Terminal)),
                detach ?? (() => Detaches++));

        public async Task<AgentPipeClient> Connect(bool expectOutput = false)
        {
            var client = await AgentPipeClient.ConnectAsync(Files.Endpoint, new HostHello(HostProtocol.Version, "client-version"), Token);
            if (!expectOutput)
            {
                await client.SendAsync(new HostFrame(HostFrameKind.Status, []), Token);
                Assert.Equal(HostFrameKind.Status, (await client.ReadAsync(Token))!.Kind);
            }
            return client;
        }
        public void Dispose() { deadline.Cancel(); Terminal.Dispose(); deadline.Dispose(); Project.Dispose(); }
    }

    private static string ReadLog(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed class Terminal : IAgentTerminal
    {
        private readonly TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Pid => 123;
        public Stream Input { get; } = new MemoryStream();
        public TerminalOutput OutputStream { get; } = new();
        public Stream Output => OutputStream;
        public Task<int> Exited => exited.Task;
        public bool Disposed { get; private set; }
        public void Resize(TerminalSize size) { }
        public void Finish() { exited.TrySetResult(17); OutputStream.End.TrySetResult(); }
        public void Stop() => Finish();
        public void Dispose()
        {
            if (Disposed) return;
            Disposed = true;
            Finish();
            Input.Dispose();
            OutputStream.Dispose();
        }
    }

    private sealed class TerminalOutput : MemoryStream
    {
        public TaskCompletionSource End { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? Text { get; set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Text is { } text)
            {
                Text = null;
                return Encoding.UTF8.GetBytes(text, buffer.Span);
            }
            await End.Task.WaitAsync(cancellationToken);
            return 0;
        }
    }
}
