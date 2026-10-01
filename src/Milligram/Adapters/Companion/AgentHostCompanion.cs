using Milligram.Adapters.Processes;
using Milligram.Application;
using Milligram.Domain.Policies;

namespace Milligram.Adapters.Companion;

/// <summary>Controls a same-user host through its pipe; discovery PIDs never authorize killing a process.</summary>
public sealed class AgentHostCompanion(ProjectPaths paths, Func<Policy> policy, IReadOnlyList<string> self, string version,
    Func<string, bool> onPath, Func<string, IReadOnlyList<string>, string, IDetachedProcess?> launch,
    Func<string?>? terminalIssue = null) : ICompanion
{
    private readonly AgentHostFiles files = new(paths);
    private readonly HostHello greeting = new(HostProtocol.Version, version);
    internal TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromMilliseconds(300);
    internal TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(20);
    internal TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(20);
    internal TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(100);
    internal Action<string>? ProbeLog { get; init; }
    internal Func<string, IReadOnlyList<string>, string, bool> LaunchTerminal { get; init; } =
        (command, args, root) => ProcessRunner.Launch(command, args, root);

    public string SessionName { get; } = TmuxCompanion.SessionNameFor(paths.Root);
    public string AttachCommand => "milligram agent attach";

    public bool IsAvailable(out string reason)
    {
        var settings = policy().Agent;
        reason = !settings.Enabled ? "The agent is disabled in milligram.json (agent.enabled)."
            : !onPath(settings.Command) ? $"'{settings.Command}' is not on PATH."
            : terminalIssue?.Invoke() ?? "";
        if (reason.Length > 0) return false;
        try
        {
            var terminal = NativeTerminalCommand.Parse(settings.Terminal, self, paths.Root);
            if (terminal is not null && !onPath(terminal.Command))
                reason = $"'{terminal.Command}' from agent.terminal is not on PATH. Install it, choose another terminal, or use 'auto' for the viewer panel.";
        }
        catch (MilligramException error) { reason = error.Message; }
        return reason.Length == 0;
    }

    public bool IsRunning() => ProbeAsync(CancellationToken.None).GetAwaiter().GetResult() is not null;

    public async Task<AgentOwnership?> StartAsync(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (await ReadyAsync(cancellation) is not null) return null;
        cancellation.ThrowIfCancellationRequested();
        if (!IsAvailable(out var reason)) throw new MilligramException(reason);
        var terminal = NativeTerminalCommand.Parse(policy().Agent.Terminal, self, paths.Root);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(StartupTimeout);
        IDetachedProcess? started = null;
        var owned = false;
        var instance = Guid.NewGuid().ToString("N");
        try
        {
            HostHello? ready;
            while ((ready = await ReadyAsync(deadline.Token)) is null)
            {
                if (LeaseIsAvailable())
                {
                    if (started?.HasExited == true)
                        throw new MilligramException(StartupFailure($"The agent host exited during startup ({started.ExitCode})."));
                    if (started is null)
                    {
                        var args = self.Skip(1).Concat(["agent", "host", "--project", paths.Root, "--instance", instance]).ToArray();
                        started = launch(self[0], args, paths.Root) ?? throw new MilligramException("Could not launch the agent host.");
                    }
                }
                await Task.Delay(RetryDelay, deadline.Token);
            }
            owned = started is { HasExited: false } && AgentHostLease.ReadDiscovery(files)?.Pid == started.Id
                && string.Equals(ready.Instance, instance, StringComparison.Ordinal);
            if (owned && terminal is not null) LaunchTerminal(terminal.Command, terminal.Args, paths.Root);
            return owned ? new AgentOwnership(() => StopAsync(instance).GetAwaiter().GetResult()) : null;
        }
        catch (OperationCanceledException)
        {
            if (cancellation.IsCancellationRequested) throw;
            throw new MilligramException(StartupFailure("The agent host did not become ready."));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new MilligramException(StartupFailure("Could not start the agent host: " + error.Message));
        }
        finally
        {
            try { if (!owned && started is { HasExited: false }) started.Stop(); }
            finally { started?.Dispose(); }
        }
    }

    public void Ring() => RingAsync().GetAwaiter().GetResult();
    public void Stop() => StopAsync(null).GetAwaiter().GetResult();
    public bool OpenTerminal()
    {
        var terminal = NativeTerminalCommand.Parse(policy().Agent.Terminal, self, paths.Root);
        return terminal is not null && IsRunning() && LaunchTerminal(terminal.Command, terminal.Args, paths.Root);
    }

    private async Task<HostHello?> ReadyAsync(CancellationToken cancellation)
    {
        var hello = await ProbeAsync(cancellation);
        var discovery = AgentHostLease.ReadDiscovery(files);
        return hello is not null && discovery is not null && string.Equals(discovery.Endpoint, files.Endpoint, StringComparison.Ordinal)
            && string.Equals(discovery.Instance, hello.Instance, StringComparison.Ordinal) ? hello : null;
    }

    private async Task<HostHello?> ProbeAsync(CancellationToken cancellation)
    {
        var trace = ProbeLog is null ? null : new AgentProbeTrace();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(ProbeTimeout);
        trace?.Mark("started");
        try
        {
            while (true)
            {
                try
                {
                    await using var client = await AgentPipeClient.ConnectAsync(files.Endpoint, greeting, deadline.Token, trace);
                    trace?.Mark("running");
                    return client.Greeting;
                }
                catch (IOException) { trace?.Mark("retry-io"); await Task.Delay(RetryDelay, deadline.Token); }
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { trace?.Mark("deadline"); return null; }
        catch (InvalidDataException) { trace?.Mark("invalid-greeting"); return null; }
        catch (Exception error) { trace?.Mark(error.GetType().Name); throw; }
        finally { if (trace is not null) ProbeLog!(trace.ToString()); }
    }

    private async Task RingAsync()
    {
        using var deadline = new CancellationTokenSource(CommandTimeout);
        try
        {
            await using var client = await AgentPipeClient.ConnectAsync(files.Endpoint, greeting, deadline.Token);
            await client.SendAsync(new HostFrame(HostFrameKind.Ring, []), deadline.Token);
            await client.SendAsync(new HostFrame(HostFrameKind.Status, []), deadline.Token);
            while (await client.ReadAsync(deadline.Token) is { } frame)
                if (frame.Kind == HostFrameKind.Status) return;
            throw new IOException("The agent exited before accepting its mail notification.");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or OperationCanceledException)
        {
            throw new MilligramException($"Could not notify the agent host. See {paths.Relative(files.LogFile)}: {error.Message}");
        }
    }

    private async Task StopAsync(string? instance)
    {
        using var deadline = new CancellationTokenSource(CommandTimeout);
        AgentHostDiscovery? original = null;
        try
        {
            if (instance is not null && !OwnsDiscovery(instance)) return;
            while (await ProbeAsync(deadline.Token) is null)
            {
                if (instance is not null && !OwnsDiscovery(instance)) return;
                if (LeaseIsAvailable()) return;
                await Task.Delay(RetryDelay, deadline.Token);
            }
            original = AgentHostLease.ReadDiscovery(files);
            await using (var client = await AgentPipeClient.ConnectAsync(files.Endpoint, greeting, deadline.Token))
            {
                if (instance is not null && !string.Equals(client.Greeting?.Instance, instance, StringComparison.Ordinal)) return;
                await client.SendAsync(new HostFrame(HostFrameKind.Stop, []), deadline.Token);
                while (await client.ReadAsync(deadline.Token) is not null) { }
            }
            while (!LeaseIsAvailable() && !HasReplacement(original, instance))
                await Task.Delay(RetryDelay, deadline.Token);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or OperationCanceledException)
        {
            while (!deadline.IsCancellationRequested)
            {
                if (LeaseIsAvailable() || HasReplacement(original, instance)) return;
                try { await Task.Delay(RetryDelay, deadline.Token); }
                catch (OperationCanceledException) { }
            }
            if (LeaseIsAvailable()) return;
            throw new MilligramException($"Could not stop the agent host. See {paths.Relative(files.LogFile)}: {error.Message}");
        }
    }

    private bool OwnsDiscovery(string instance) =>
        string.Equals(AgentHostLease.ReadDiscovery(files)?.Instance, instance, StringComparison.Ordinal);

    /// <summary>Discovery disappears before its lease closes; only a published replacement permits leaving early.</summary>
    private bool HasReplacement(AgentHostDiscovery? original, string? instance)
    {
        var current = AgentHostLease.ReadDiscovery(files);
        if (current is null) return false;
        return original is not null && current != original || instance is not null && !string.Equals(current.Instance, instance, StringComparison.Ordinal);
    }

    private bool LeaseIsAvailable()
    {
        try { using var lease = new AgentHostLease(files); return true; }
        catch (IOException) { return false; }
    }

    private string StartupFailure(string message)
    {
        try
        {
            using var stream = new FileStream(files.LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.Seek(Math.Max(0, stream.Length - 4096), SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            var buffer = new char[4096];
            var tail = new string(buffer, 0, reader.ReadBlock(buffer, 0, buffer.Length)).Trim();
            if (tail.Length > 0) message += "\n" + tail;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return message + $"\nSee {paths.Relative(files.LogFile)}.";
    }
}
