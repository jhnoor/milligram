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
            : !ProcessRunner.OnPath("tmux") ? "tmux is not installed."
            : !ProcessRunner.OnPath(settings.Command) ? $"'{settings.Command}' is not on PATH."
            : "";
        return reason.Length == 0;
    }

    public bool IsRunning() => Supported && ProcessRunner.OnPath("tmux") && Tmux("has-session", "-t", SessionName) == 0;

    /// <summary>Not native Windows: a tmux from MSYS2 or Cygwin can't run the bash launch script with Windows paths in it.</summary>
    private static bool Supported => !OperatingSystem.IsWindows();

    public Task StartAsync(CancellationToken cancellation)
    {
        if (IsRunning()) return Task.CompletedTask;
        if (!IsAvailable(out var reason)) throw new MilligramException(reason);

        AgentBriefing.Write(paths);
        var script = WriteLaunchScript();
        var code = Tmux("new-session", "-d", "-s", SessionName, "-c", paths.Root, "-x", "200", "-y", "50", $"bash {AgentLaunches.BashQuote(script)}");
        if (code != 0) throw new MilligramException($"tmux could not start session {SessionName}.");
        OpenTerminal();
        return Task.CompletedTask;
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

    private static int Tmux(params string[] args) => ProcessRunner.Capture("tmux", args).ExitCode;
}