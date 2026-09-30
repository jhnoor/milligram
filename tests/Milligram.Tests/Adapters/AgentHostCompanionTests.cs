using System.Text;
using System.IO.Pipes;
using Milligram.Adapters.Companion;
using Milligram.Adapters.Processes;
using Milligram.Application;
using Milligram.Domain.Policies;

namespace Milligram.Tests.Adapters;

public class AgentHostCompanionTests
{
    [Fact]
    public async Task StartingTwiceReusesTheHostAndKeepsTheProjectSessionName()
    {
        await using var fixture = new Fixture();
        await fixture.Companion.StartAsync(fixture.Token);
        await fixture.Companion.StartAsync(fixture.Token);
        Assert.Equal(1, fixture.Launches);
        Assert.True(await Task.Run(fixture.Companion.IsRunning));
        Assert.Equal(TmuxCompanion.SessionNameFor(fixture.Paths.Root), fixture.Companion.SessionName);
        Assert.Equal("milligram agent attach", fixture.Companion.AttachCommand);
        Assert.Equal(["milligram-stub.dll", "agent", "host", "--project", fixture.Paths.Root], fixture.Arguments);
        Assert.True(fixture.Process!.Disposed);
        Assert.Equal(0, fixture.Process.Stops);
    }

    [Fact]
    public async Task ANotificationReturnsOnlyAfterTheDoorbellAndReturnAreWritten()
    {
        await using var fixture = new Fixture();
        await fixture.Companion.StartAsync(fixture.Token);
        await Task.Run(fixture.Companion.Ring);
        Assert.Equal(AgentBriefing.Doorbell + "\r", fixture.Terminal.Text);
        Assert.False(fixture.Running!.IsCompleted);
    }

    [Fact]
    public async Task StopWaitsForOwnershipToEndAndAnImmediateRestartCanSucceed()
    {
        await using var fixture = new Fixture();
        await fixture.Companion.StartAsync(fixture.Token);
        await Task.Run(fixture.Companion.Stop);
        Assert.Equal(0, await fixture.Running!.WaitAsync(fixture.Token));
        Assert.Null(AgentHostLease.ReadDiscovery(fixture.Files));
        using (var free = new AgentHostLease(fixture.Files)) { }
        fixture.Terminal = new Terminal();
        await fixture.Companion.StartAsync(fixture.Token);
        Assert.Equal(2, fixture.Launches);
        Assert.True(await Task.Run(fixture.Companion.IsRunning));
    }

    [Fact]
    public async Task StaleDiscoveryDoesNotMakeAnAbsentHostRunOrAuthorizeProcessControl()
    {
        await using var fixture = new Fixture();
        JsonFile.Write(fixture.Files.DiscoveryFile, new AgentHostDiscovery(Environment.ProcessId, fixture.Files.Endpoint, 1, "old", DateTimeOffset.UnixEpoch));
        Assert.False(await Task.Run(fixture.Companion.IsRunning));
        await Task.Run(fixture.Companion.Stop);
        Assert.Equal(0, fixture.Launches);
        Assert.Null(fixture.Process);
        await fixture.Companion.StartAsync(fixture.Token);
        Assert.NotEqual("old", AgentHostLease.ReadDiscovery(fixture.Files)!.Version);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DisabledOrMissingCommandsExplainWhyStartupIsUnavailable(bool disabled)
    {
        await using var fixture = new Fixture();
        fixture.Policy = fixture.Policy with { Agent = fixture.Policy.Agent with { Enabled = !disabled } };
        fixture.Available = disabled;
        Assert.False(fixture.Companion.IsAvailable(out var reason));
        Assert.Contains(disabled ? "agent.enabled" : "not on PATH", reason);
        Assert.Equal(reason, (await Assert.ThrowsAsync<MilligramException>(() => fixture.Companion.StartAsync(fixture.Token))).Message);
        Assert.Equal(0, fixture.Launches);
    }

    [Fact]
    public async Task CancellationBeforeStartupNeverLaunchesAProcess()
    {
        await using var fixture = new Fixture();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Companion.StartAsync(new CancellationToken(true)));
        Assert.Equal(0, fixture.Launches);
    }

    [Fact]
    public async Task CancellationDuringStartupStopsOnlyTheOwnedChildAndDisposesItsHandle()
    {
        await using var fixture = new Fixture();
        fixture.SpawnGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var starting = fixture.Companion.StartAsync(cancellation.Token);
        await fixture.Spawning.Task.WaitAsync(fixture.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);
        Assert.Equal(1, fixture.Process!.Stops);
        Assert.True(fixture.Process.Disposed);
        await fixture.Running!.WaitAsync(fixture.Token);
        Assert.Null(AgentHostLease.ReadDiscovery(fixture.Files));
    }

    [Fact]
    public async Task StartupTimeoutStopsTheOwnedChildAndPointsToItsLog()
    {
        await using var fixture = new Fixture(timeout: TimeSpan.FromMilliseconds(150));
        fixture.SpawnGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var error = await Assert.ThrowsAsync<MilligramException>(() => fixture.Companion.StartAsync(fixture.Token));
        Assert.Contains("did not become ready", error.Message);
        Assert.Contains("agent-host.log", error.Message);
        Assert.Equal(1, fixture.Process!.Stops);
        Assert.True(fixture.Process.Disposed);
    }

    [Fact]
    public async Task AnEarlyHostExitReportsTheActualStartupFailureWithoutRespawning()
    {
        await using var fixture = new Fixture();
        fixture.SpawnError = new IOException("native startup broke");
        var error = await Assert.ThrowsAsync<MilligramException>(() => fixture.Companion.StartAsync(fixture.Token));
        Assert.Contains("exited during startup (1)", error.Message);
        Assert.Contains("native startup broke", error.Message);
        Assert.Equal(1, fixture.Launches);
        Assert.Equal(0, fixture.Process!.Stops);
        Assert.True(fixture.Process.Disposed);
    }

    [Fact]
    public async Task FailureToLaunchProducesAnActionableError()
    {
        await using var fixture = new Fixture();
        fixture.LaunchFails = true;
        var error = await Assert.ThrowsAsync<MilligramException>(() => fixture.Companion.StartAsync(fixture.Token));
        Assert.Equal("Could not launch the agent host.", error.Message);
        Assert.Equal(1, fixture.Launches);
    }

    [Fact]
    public async Task AClosingOwnerMustReleaseTheLeaseBeforeTheNextChildIsLaunched()
    {
        await using var fixture = new Fixture();
        var owner = new AgentHostLease(fixture.Files);
        var starting = fixture.Companion.StartAsync(fixture.Token);
        await Task.Delay(100, fixture.Token);
        Assert.False(starting.IsCompleted);
        Assert.Equal(0, fixture.Launches);
        owner.Dispose();
        await starting;
        Assert.Equal(1, fixture.Launches);
    }

    [Fact]
    public async Task StopDuringInitializationWaitsForTheHostToAcceptItsCommand()
    {
        await using var fixture = new Fixture();
        fixture.SpawnGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starting = fixture.Companion.StartAsync(fixture.Token);
        await fixture.Spawning.Task.WaitAsync(fixture.Token);
        var stopping = Task.Run(fixture.Companion.Stop);
        await Task.Delay(100, fixture.Token);
        Assert.False(stopping.IsCompleted);
        fixture.SpawnGate.TrySetResult();
        await stopping.WaitAsync(fixture.Token);
        try { await starting; }
        catch (MilligramException error) { Assert.Contains("exited during startup", error.Message); }
        Assert.Equal(0, await fixture.Running!.WaitAsync(fixture.Token));
        Assert.Null(AgentHostLease.ReadDiscovery(fixture.Files));
    }

    [Fact]
    public async Task ANotificationToAnAbsentHostTimesOutWithAUsefulMessage()
    {
        await using var fixture = new Fixture(timeout: TimeSpan.FromMilliseconds(100));
        var error = await Assert.ThrowsAsync<MilligramException>(() => Task.Run(fixture.Companion.Ring));
        Assert.Contains("Could not notify", error.Message);
        Assert.Contains("agent-host.log", error.Message);
    }

    [Fact]
    public async Task AnUnresponsiveOwnerCannotHoldStopIndefinitely()
    {
        await using var fixture = new Fixture(timeout: TimeSpan.FromMilliseconds(150));
        using var owner = new AgentHostLease(fixture.Files);
        var error = await Assert.ThrowsAsync<MilligramException>(() => Task.Run(fixture.Companion.Stop));
        Assert.Contains("Could not stop", error.Message);
        Assert.Null(fixture.Process);
        Assert.Throws<IOException>(() => new AgentHostLease(fixture.Files));
    }

    [Fact]
    public async Task StartupTimeoutWhileAnotherOwnerHoldsTheLeaseNeverLaunchesOrStopsAChild()
    {
        await using var fixture = new Fixture(timeout: TimeSpan.FromMilliseconds(150));
        using var owner = new AgentHostLease(fixture.Files);
        await Assert.ThrowsAsync<MilligramException>(() => fixture.Companion.StartAsync(fixture.Token));
        Assert.Equal(0, fixture.Launches);
        Assert.Null(fixture.Process);
        Assert.Throws<IOException>(() => new AgentHostLease(fixture.Files));
    }

    [Fact]
    public async Task ACorruptGreetingIsNotReportedAsARunningHost()
    {
        await using var fixture = new Fixture();
        using var server = new NamedPipeServerStream(fixture.Files.Endpoint, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var accepting = server.WaitForConnectionAsync(fixture.Token);
        var probing = Task.Run(fixture.Companion.IsRunning);
        await accepting;
        await HostProtocol.ReadAsync(server, fixture.Token);
        await HostProtocol.WriteAsync(server, new HostFrame(HostFrameKind.Output, []), fixture.Token);
        Assert.False(await probing.WaitAsync(fixture.Token));
    }

    [Fact]
    public async Task AnIncompatibleHostMustBeRestartedWithoutLaunchingAnotherOne()
    {
        await using var fixture = new Fixture();
        using var server = new NamedPipeServerStream(fixture.Files.Endpoint, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var accepting = server.WaitForConnectionAsync(fixture.Token);
        var starting = fixture.Companion.StartAsync(fixture.Token);
        await accepting;
        await HostProtocol.ReadAsync(server, fixture.Token);
        await HostProtocol.WriteAsync(server, HostProtocol.Json(HostFrameKind.Hello, new HostHello(2, "future")), fixture.Token);
        var error = await Assert.ThrowsAsync<MilligramException>(() => starting);
        Assert.Contains("protocol 2", error.Message);
        Assert.Contains("future", error.Message);
        Assert.Contains("restart", error.Message);
        Assert.Equal(0, fixture.Launches);
    }

    [Fact]
    public async Task StartupWaitsForDiscoveryEvenWhenTheListenerAlreadyAnswersHello()
    {
        await using var fixture = new Fixture();
        using var owner = new AgentHostLease(fixture.Files);
        using var session = new AgentSession(fixture.Terminal, new TerminalSize(80, 24));
        using var server = new AgentPipeServer(session, fixture.Files.Endpoint, new HostHello(HostProtocol.Version, "test-version"), _ => { });
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(fixture.Token);
        var listening = server.RunAsync(cancellation.Token);
        try
        {
            await server.Ready;
            var starting = fixture.Companion.StartAsync(fixture.Token);
            await Task.Delay(100, fixture.Token);
            Assert.False(starting.IsCompleted);
            Assert.Equal(0, fixture.Launches);
            owner.Publish(new AgentHostDiscovery(Environment.ProcessId, fixture.Files.Endpoint, HostProtocol.Version, "test-version", DateTimeOffset.UtcNow));
            await starting;
            Assert.Equal(0, fixture.Launches);
        }
        finally { cancellation.Cancel(); await listening.WaitAsync(fixture.Token); }
    }

    [Fact]
    public async Task StartupErrorsIncludeOnlyABoundedTailOfTheHostLog()
    {
        await using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Files.Directory);
        File.WriteAllText(fixture.Files.LogFile, "old log prefix\n" + new string('x', 8000) + "\n");
        fixture.SpawnError = new IOException("latest native failure");
        var error = await Assert.ThrowsAsync<MilligramException>(() => fixture.Companion.StartAsync(fixture.Token));
        Assert.Contains("latest native failure", error.Message);
        Assert.DoesNotContain("old log prefix", error.Message);
        Assert.InRange(error.Message.Length, 1, 4400);
    }

    [Fact]
    public async Task ALaunchIoFailureIncludesTheCauseAndLogLocation()
    {
        await using var fixture = new Fixture();
        fixture.LaunchError = new IOException("launch access failed");
        var error = await Assert.ThrowsAsync<MilligramException>(() => fixture.Companion.StartAsync(fixture.Token));
        Assert.Contains("Could not start the agent host: launch access failed", error.Message);
        Assert.Contains("agent-host.log", error.Message);
        Assert.Equal(1, fixture.Launches);
    }

    [Fact]
    public async Task AHostClosingBeforeItsNotificationBarrierReportsFailure()
    {
        await using var fixture = new Fixture();
        using var server = Server(fixture.Files.Endpoint);
        var ringing = Task.Run(fixture.Companion.Ring);
        await Greet(server, fixture.Token);
        Assert.Equal(HostFrameKind.Ring, (await HostProtocol.ReadAsync(server, fixture.Token))!.Kind);
        Assert.Equal(HostFrameKind.Status, (await HostProtocol.ReadAsync(server, fixture.Token))!.Kind);
        server.Dispose();
        var error = await Assert.ThrowsAsync<MilligramException>(() => ringing);
        Assert.Contains("exited before accepting", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopWaitsForOwnershipAfterTheTransportCloses(bool corruptReply)
    {
        await using var fixture = new Fixture();
        using var owner = new AgentHostLease(fixture.Files);
        owner.Publish(new AgentHostDiscovery(Environment.ProcessId, fixture.Files.Endpoint, HostProtocol.Version, "test-version", DateTimeOffset.UtcNow));
        using var probe = Server(fixture.Files.Endpoint);
        using var commands = Server(fixture.Files.Endpoint);
        var stopping = Task.Run(fixture.Companion.Stop);
        await Greet(probe, fixture.Token);
        probe.Dispose();
        await Greet(commands, fixture.Token);
        Assert.Equal(HostFrameKind.Stop, (await HostProtocol.ReadAsync(commands, fixture.Token))!.Kind);
        if (corruptReply) await commands.WriteAsync(new byte[] { 0, 0, 0, 0 }, fixture.Token);
        commands.Dispose();
        await Task.Delay(100, fixture.Token);
        Assert.False(stopping.IsCompleted);
        owner.Dispose();
        await stopping.WaitAsync(fixture.Token);
        using var free = new AgentHostLease(fixture.Files);
    }

    private static NamedPipeServerStream Server(string endpoint) => new(endpoint, PipeDirection.InOut,
        NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private static async Task Greet(NamedPipeServerStream server, CancellationToken cancellation)
    {
        await server.WaitForConnectionAsync(cancellation);
        Assert.Equal(HostFrameKind.Hello, (await HostProtocol.ReadAsync(server, cancellation))!.Kind);
        await HostProtocol.WriteAsync(server, HostProtocol.Json(HostFrameKind.Hello, new HostHello(HostProtocol.Version, "test-version")), cancellation);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TempProject project = new();
        private readonly CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        private readonly CancellationTokenSource hostLifetime = new();
        public ProjectPaths Paths { get; }
        public AgentHostFiles Files { get; }
        public Policy Policy { get; set; } = new() { Agent = new AgentSettings { Command = "fixture-agent" } };
        public AgentHostCompanion Companion { get; }
        public Terminal Terminal { get; set; } = new();
        public ProcessHandle? Process { get; private set; }
        public Task<int>? Running { get; private set; }
        public TaskCompletionSource Spawning { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? SpawnGate { get; set; }
        public Exception? SpawnError { get; set; }
        public bool LaunchFails { get; set; }
        public Exception? LaunchError { get; set; }
        public bool Available { get; set; } = true;
        public int Launches { get; private set; }
        public IReadOnlyList<string>? Arguments { get; private set; }
        public CancellationToken Token => deadline.Token;

        public Fixture(TimeSpan? timeout = null)
        {
            Paths = new ProjectPaths(project.Root);
            Files = new AgentHostFiles(Paths);
            Companion = new AgentHostCompanion(Paths, () => Policy, ["dotnet-stub", "milligram-stub.dll"], "test-version", _ => Available, Launch)
            {
                ProbeTimeout = TimeSpan.FromMilliseconds(20),
                StartupTimeout = timeout ?? TimeSpan.FromSeconds(3),
                CommandTimeout = timeout ?? TimeSpan.FromSeconds(3),
                RetryDelay = TimeSpan.FromMilliseconds(10),
            };
        }

        private IDetachedProcess? Launch(string command, IReadOnlyList<string> args, string root)
        {
            Assert.Equal("dotnet-stub", command);
            Assert.Equal(Paths.Root, root);
            Launches++;
            Arguments = args;
            if (LaunchError is { } error) throw error;
            if (LaunchFails) return null;
            var host = new AgentHost(Paths, () => Policy, ["milligram-stub"], "test-version", async (_, _, cancellation) =>
            {
                Spawning.TrySetResult();
                if (SpawnError is { } error) throw error;
                if (SpawnGate is not null) await SpawnGate.Task.WaitAsync(cancellation);
                return Terminal;
            }, () => { });
            Running = host.RunAsync(hostLifetime.Token);
            return Process = new ProcessHandle(Running, hostLifetime.Cancel);
        }

        public async ValueTask DisposeAsync()
        {
            hostLifetime.Cancel();
            Terminal.Finish();
            try { if (Running is not null) await Running.WaitAsync(Token); }
            finally { hostLifetime.Dispose(); deadline.Dispose(); project.Dispose(); }
        }
    }

    private sealed class ProcessHandle(Task<int> running, Action stop) : IDetachedProcess
    {
        public int Id => Environment.ProcessId;
        public bool HasExited => running.IsCompleted;
        public int ExitCode => running.GetAwaiter().GetResult();
        public int Stops { get; private set; }
        public bool Disposed { get; private set; }
        public void Stop() { Stops++; stop(); }
        public void Dispose() => Disposed = true;
    }

    private sealed class Terminal : IAgentTerminal
    {
        private readonly TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly MemoryStream input = new();
        public int Pid => 123;
        public Stream Input => input;
        public Stream Output { get; }
        public Task<int> Exited => exited.Task;
        public string Text => Encoding.UTF8.GetString(input.ToArray());
        public Terminal() => Output = new OutputStream(Exited);
        public void Finish() => exited.TrySetResult(17);
        public void Resize(TerminalSize size) { }
        public void Stop() => Finish();
        public void Dispose() { Finish(); input.Dispose(); Output.Dispose(); }
    }

    private sealed class OutputStream(Task<int> exited) : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await exited.WaitAsync(cancellationToken);
            return 0;
        }
    }
}
