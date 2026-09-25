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
public sealed class TmuxCompanion(ProjectPaths paths, Func<Policy> policy, string selfCommand) : ICompanion
{
    private sealed record AgentState(string SessionId, DateTimeOffset CreatedAt);

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
            : !ProcessRunner.OnPath("tmux") ? "tmux is not installed."
            : !ProcessRunner.OnPath(settings.Command) ? $"'{settings.Command}' is not on PATH."
            : "";
        return reason.Length == 0;
    }

    public bool IsRunning() => ProcessRunner.OnPath("tmux") && Tmux("has-session", "-t", SessionName) == 0;

    public Task StartAsync(CancellationToken cancellation)
    {
        if (IsRunning()) return Task.CompletedTask;
        if (!IsAvailable(out var reason)) throw new MilligramException(reason);

        AgentBriefing.Write(paths);
        var script = WriteLaunchScript();
        var code = Tmux("new-session", "-d", "-s", SessionName, "-c", paths.Root, "-x", "200", "-y", "50", $"bash {Quote(script)}");
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
        var settings = policy().Agent;
        var (state, resumed) = LoadOrCreateState();
        var shim = WriteShim();
        var command = Path.GetFileName(settings.Command) == "copilot"
            ? CopilotCommand(settings, state, resumed)
            : [settings.Command, .. settings.Args];

        var script = Path.Combine(paths.RunDirectory, "agent.sh");
        File.WriteAllText(script, $"""
            #!/usr/bin/env bash
            # Written by milligram: starts the companion agent inside tmux.
            export PATH={Quote(shim)}:"$PATH"
            cd {Quote(paths.Root)} || exit 1
            exec {string.Join(' ', command.Select(Quote))}

            """);
        return script;
    }

    private IReadOnlyList<string> CopilotCommand(AgentSettings settings, AgentState state, bool resumed)
    {
        var args = new List<string> { settings.Command, "--session-id", state.SessionId };
        if (!resumed) args.AddRange(["--name", $"Milligram: {Path.GetFileName(paths.Root)}"]);
        foreach (var tool in settings.AllowTools) args.AddRange(["--allow-tool", tool]);
        args.AddRange(["--allow-tool", $"write({paths.PolicyFile})"]);
        if (settings.Model is { Length: > 0 } model) args.AddRange(["--model", model]);
        args.AddRange(settings.Args);
        args.AddRange(["-i", resumed ? AgentBriefing.ResumePrompt : AgentBriefing.LaunchPrompt]);
        return args;
    }

    /// <summary>Keeps one agent conversation per project, resumed on every start.</summary>
    private (AgentState State, bool Resumed) LoadOrCreateState()
    {
        if (JsonFile.Read<AgentState>(paths.AgentStateFile) is { SessionId.Length: > 0 } existing) return (existing, true);
        var created = new AgentState(Guid.NewGuid().ToString(), DateTimeOffset.UtcNow);
        JsonFile.Write(paths.AgentStateFile, created);
        return (created, false);
    }

    /// <summary>A `milligram` on the agent's PATH that runs this very build of the tool.</summary>
    private string WriteShim()
    {
        var directory = Path.Combine(paths.RunDirectory, "bin");
        Directory.CreateDirectory(directory);
        var shim = Path.Combine(directory, "milligram");
        File.WriteAllText(shim, $"#!/usr/bin/env bash\nexec {selfCommand} \"$@\"\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(shim, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                       UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return directory;
    }

    public static string Quote(string word) => "'" + word.Replace("'", "'\\''") + "'";

    private static int Tmux(params string[] args) => ProcessRunner.Capture("tmux", args).ExitCode;
}
