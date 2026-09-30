using System.Text;
using Milligram.Application;
using Milligram.Domain.Policies;

namespace Milligram.Adapters.Companion;

/// <summary>Owns one project's lease, briefing, terminal and discovery for the lifetime of a detached host.</summary>
public sealed class AgentHost(ProjectPaths paths, Func<Policy> policy, IReadOnlyList<string> self, string version,
    Func<AgentLaunch, TerminalSize, CancellationToken, Task<IAgentTerminal>> startTerminal, Action detach)
{
    public async Task<int> RunAsync(CancellationToken cancellation)
    {
        var files = new AgentHostFiles(paths);
        AgentHostLease lease;
        try { lease = new AgentHostLease(files); }
        catch (IOException) { return 1; }
        using var ownership = lease;
        using var writer = new StreamWriter(new FileStream(files.LogFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false))
        {
            AutoFlush = true,
        };
        var log = TextWriter.Synchronized(writer);
        void Log(string message) => log.WriteLine($"{DateTimeOffset.UtcNow:O} {message}");
        try
        {
            cancellation.ThrowIfCancellationRequested();
            detach();
            var started = DateTimeOffset.UtcNow;
            Log($"Agent host {Environment.ProcessId} starting (Milligram {version}).");
            if (!policy().Agent.Enabled) throw new MilligramException("The agent is disabled in milligram.json (agent.enabled).");
            AgentBriefing.Write(paths);
            var launch = new AgentLaunches(paths, policy, self).Prepare(OperatingSystem.IsWindows());
            var size = new TerminalSize(80, 24);
            using var terminal = await startTerminal(launch, size, cancellation);
            using var session = new AgentSession(terminal, size);
            using var pipe = new AgentPipeServer(session, files.Endpoint, new HostHello(HostProtocol.Version, version), Log);
            var runtime = new AgentHostRuntime(terminal, session, pipe, Log);
            return await runtime.RunAsync(() =>
            {
                lease.Publish(new AgentHostDiscovery(Environment.ProcessId, files.Endpoint, HostProtocol.Version, version, started));
                Log($"Agent host ready; agent pid {terminal.Pid}.");
            }, cancellation);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return 0; }
        catch (Exception error)
        {
            Log("Agent host failed: " + error.Message);
            return 1;
        }
    }
}
