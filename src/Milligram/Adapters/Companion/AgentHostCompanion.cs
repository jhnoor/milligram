using Milligram.Adapters.Processes;
using Milligram.Application;
using Milligram.Domain.Policies;

namespace Milligram.Adapters.Companion;

/// <summary>Controls a same-user host through its pipe; discovery PIDs never authorize killing a process.</summary>
public sealed class AgentHostCompanion(ProjectPaths paths, Func<Policy> policy, IReadOnlyList<string> self, string version,
    Func<string, bool> onPath, Func<string, IReadOnlyList<string>, string, IDetachedProcess?> launch) : ICompanion
{
    private readonly AgentHostFiles files = new(paths);
    private readonly HostHello greeting = new(HostProtocol.Version, version);
    internal TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromMilliseconds(300);
    internal TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(20);
    internal TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(20);
    internal TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(100);

    public string SessionName { get; } = TmuxCompanion.SessionNameFor(paths.Root);
    public string AttachCommand => "milligram agent attach";

    public bool IsAvailable(out string reason)
    {
        var settings = policy().Agent;
        reason = !settings.Enabled ? "The agent is disabled in milligram.json (agent.enabled)."
            : !onPath(settings.Command) ? $"'{settings.Command}' is not on PATH."
            : "";
        return reason.Length == 0;
    }

    public bool IsRunning() => ProbeAsync(CancellationToken.None).GetAwaiter().GetResult();

    public async Task StartAsync(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (await ReadyAsync(cancellation)) return;
        cancellation.ThrowIfCancellationRequested();
        if (!IsAvailable(out var reason)) throw new MilligramException(reason);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(StartupTimeout);
        IDetachedProcess? started = null;
        var ready = false;
        try
        {
            while (!await ReadyAsync(deadline.Token))
            {
                if (LeaseIsAvailable())
                {
                    if (started?.HasExited == true)
                        throw new MilligramException(StartupFailure($"The agent host exited during startup ({started.ExitCode})."));
                    if (started is null)
                    {
                        var args = self.Skip(1).Concat(["agent", "host", "--project", paths.Root]).ToArray();
                        started = launch(self[0], args, paths.Root) ?? throw new MilligramException("Could not launch the agent host.");
                    }
                }
                await Task.Delay(RetryDelay, deadline.Token);
            }
            ready = true;
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
            try { if (!ready && started is { HasExited: false }) started.Stop(); }
            finally { started?.Dispose(); }
        }
    }

    public void Ring() => RingAsync().GetAwaiter().GetResult();
    public void Stop() => StopAsync().GetAwaiter().GetResult();
    public bool OpenTerminal() => false;

    private async Task<bool> ReadyAsync(CancellationToken cancellation) =>
        await ProbeAsync(cancellation) && string.Equals(AgentHostLease.ReadDiscovery(files)?.Endpoint, files.Endpoint, StringComparison.Ordinal);

    private async Task<bool> ProbeAsync(CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(ProbeTimeout);
        try
        {
            await using var client = await AgentPipeClient.ConnectAsync(files.Endpoint, greeting, deadline.Token);
            return true;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { return false; }
        catch (Exception error) when (error is IOException or InvalidDataException) { return false; }
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

    private async Task StopAsync()
    {
        using var deadline = new CancellationTokenSource(CommandTimeout);
        AgentHostDiscovery? original = null;
        try
        {
            while (!await ProbeAsync(deadline.Token))
            {
                if (LeaseIsAvailable()) return;
                await Task.Delay(RetryDelay, deadline.Token);
            }
            original = AgentHostLease.ReadDiscovery(files);
            await using (var client = await AgentPipeClient.ConnectAsync(files.Endpoint, greeting, deadline.Token))
            {
                await client.SendAsync(new HostFrame(HostFrameKind.Stop, []), deadline.Token);
                while (await client.ReadAsync(deadline.Token) is not null) { }
            }
            while (!LeaseIsAvailable() && (original is null || AgentHostLease.ReadDiscovery(files) == original))
                await Task.Delay(RetryDelay, deadline.Token);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or OperationCanceledException)
        {
            while (!deadline.IsCancellationRequested)
            {
                if (LeaseIsAvailable() || original is not null && AgentHostLease.ReadDiscovery(files) != original) return;
                try { await Task.Delay(RetryDelay, deadline.Token); }
                catch (OperationCanceledException) { }
            }
            if (LeaseIsAvailable()) return;
            throw new MilligramException($"Could not stop the agent host. See {paths.Relative(files.LogFile)}: {error.Message}");
        }
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
