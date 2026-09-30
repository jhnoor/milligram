using System.Security.Cryptography;
using System.Text;
using Milligram.Adapters.Processes;
using Milligram.Application;
using Milligram.Domain.Policies;

namespace Milligram.Adapters.Companion;

/// <summary>
/// Runs the companion agent (GitHub Copilot CLI by default) in a tmux session unique to the examined
/// project. tmux is only the doorbell: messages travel through the .milligram/mail directories.
/// </summary>
public sealed class TmuxCompanion(ProjectPaths paths, Func<Policy> policy, IReadOnlyList<string> self) : ICompanion
{
    private readonly AgentLaunches launches = new(paths, policy, self);
    internal Func<string, bool> OnPath { get; init; } = ProcessRunner.OnPath;
    internal Func<string[], (int ExitCode, string Output)> Capture { get; init; } = args => ProcessRunner.Capture("tmux", args);

    public string SessionName { get; } = SessionNameFor(paths.Root);

    public string AttachCommand => $"tmux attach -t {SessionName}";

    public static string SessionNameFor(string root)
    {
        var name = new string(Path.GetFileName(root).Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '-').ToArray());
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(root)))[..8].ToLowerInvariant();
        return $"milligram-{name}-{hash}";
    }

    public bool IsAvailable(out string reason)
    {
        var settings = policy().Agent;
        reason = !settings.Enabled ? "The agent is disabled in milligram.json (agent.enabled)."
            : !Supported ? "tmux, which the agent runs in, doesn't run on native Windows."
            : !OnPath("tmux") ? "tmux is not installed."
            : !OnPath(settings.Command) ? $"'{settings.Command}' is not on PATH."
            : "";
        return reason.Length == 0;
    }

    public bool IsRunning() => Supported && OnPath("tmux") && Tmux("has-session", "-t", SessionName) == 0;

    /// <summary>Not native Windows: a tmux from MSYS2 or Cygwin can't run the bash launch script with Windows paths in it.</summary>
    internal bool Supported { get; init; } = !OperatingSystem.IsWindows();

    public Task<AgentOwnership?> StartAsync(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (IsRunning()) return Task.FromResult<AgentOwnership?>(null);
        if (!IsAvailable(out var reason)) throw new MilligramException(reason);

        AgentBriefing.Write(paths);
        var script = WriteLaunchScript();
        var instance = Guid.NewGuid().ToString("N");
        var (code, output) = Capture(["new-session", "-d", "-s", SessionName, "-c", paths.Root, "-x", "200", "-y", "50",
            "-e", "MILLIGRAM_SESSION_OWNER=" + instance, "-P", "-F", "#{session_id}", $"bash {AgentLaunches.BashQuote(script)}"]);
        if (code != 0)
        {
            if (IsRunning()) return Task.FromResult<AgentOwnership?>(null);
            throw new MilligramException($"tmux could not start session {SessionName}.");
        }
        var id = output.Trim();
        if (id.Length < 2 || id[0] != '$' || id.Skip(1).Any(c => c is < '0' or > '9'))
            throw new MilligramException($"tmux did not identify the new session {SessionName}.");
        OpenTerminal();
        return Task.FromResult<AgentOwnership?>(new(() => StopOwned(id, instance)));
    }

    public void Stop()
    {
        if (IsRunning()) Tmux("kill-session", "-t", SessionName);
    }

    public void Ring()
    {
        Tmux("send-keys", "-t", SessionName, "-l", AgentBriefing.Doorbell);
        Thread.Sleep(150);
        Tmux("send-keys", "-t", SessionName, "Enter");
    }

    public bool OpenTerminal() => IsRunning() && Desktop.OpenTerminal(policy().Agent.Terminal, SessionName, paths.Root);

    private string WriteLaunchScript()
    {
        var script = Path.Combine(paths.RunDirectory, "agent.sh");
        File.WriteAllText(script, Script(launches.Prepare(windows: false)));
        return script;
    }

    /// <summary>The launch as the bash script that tmux runs.</summary>
    public static string Script(AgentLaunch launch) => $"""
        #!/usr/bin/env bash
        # Written by milligram: starts the companion agent inside tmux.
        export PATH={AgentLaunches.BashQuote(launch.ShimDirectory)}:"$PATH"
        cd {AgentLaunches.BashQuote(launch.WorkingDirectory)} || exit 1
        exec {string.Join(' ', launch.Args.Prepend(launch.Command).Select(AgentLaunches.BashQuote))}

        """;

    private void StopOwned(string id, string instance) => Tmux("if-shell", "-F", "-t", id,
        "#{==:#{MILLIGRAM_SESSION_OWNER}," + instance + "}", $"kill-session -t '{id}'");

    private int Tmux(params string[] args) => Capture(args).ExitCode;
}
