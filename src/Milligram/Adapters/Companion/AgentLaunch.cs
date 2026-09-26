using System.Text;
using Milligram.Application;
using Milligram.Domain.Policies;

namespace Milligram.Adapters.Companion;

/// <summary>
/// How to start the companion agent, the same for every companion: the tmux companion renders it into a bash script,
/// and a companion that owns the terminal starts it directly. <see cref="ShimDirectory"/> goes first on the agent's
/// PATH, so that `milligram` there runs this very build of the tool.
/// </summary>
public sealed record AgentLaunch(string Command, IReadOnlyList<string> Args, string WorkingDirectory, string ShimDirectory)
{
    /// <summary>The agent's PATH: the shim directory, then what it inherits, joined with the OS's separator.</summary>
    public string PathFor(string? inherited, char separator) =>
        string.IsNullOrEmpty(inherited) ? ShimDirectory : ShimDirectory + separator + inherited;
}

/// <summary>The agent's side of one conversation per project, resumed on every start.</summary>
public sealed record AgentConversation(string SessionId, DateTimeOffset CreatedAt);

/// <summary>Builds the <see cref="AgentLaunch"/>: the agent's command, its conversation, and the `milligram` shim.</summary>
public sealed class AgentLaunches(ProjectPaths paths, Func<Policy> policy, IReadOnlyList<string> self)
{
    /// <summary>Writes the shim and, for a new conversation, its state; then describes the launch.</summary>
    public AgentLaunch Prepare(bool windows)
    {
        var settings = policy().Agent;
        var (conversation, resumed) = LoadOrCreate();
        var args = IsCopilot(settings.Command)
            ? CopilotArgs(settings, conversation, resumed, paths)
            : settings.Args;
        return new AgentLaunch(settings.Command, args, paths.Root, WriteShim(windows));
    }

    public static bool IsCopilot(string command) =>
        Path.GetFileNameWithoutExtension(command.Replace('\\', '/').Split('/')[^1]) == "copilot";

    /// <summary>
    /// Copilot joins the project's conversation, may run `milligram` and edit milligram.json without asking, and is told
    /// to read its briefing: at once in a new conversation, or again after a restart in a resumed one.
    /// </summary>
    public static IReadOnlyList<string> CopilotArgs(AgentSettings settings, AgentConversation conversation, bool resumed, ProjectPaths paths)
    {
        var args = new List<string> { "--session-id", conversation.SessionId };
        if (!resumed) args.AddRange(["--name", $"Milligram: {Path.GetFileName(paths.Root)}"]);
        foreach (var tool in settings.AllowTools) args.AddRange(["--allow-tool", tool]);
        args.AddRange(["--allow-tool", $"write({paths.PolicyFile})"]);
        if (settings.Model is { Length: > 0 } model) args.AddRange(["--model", model]);
        args.AddRange(settings.Args);
        args.AddRange(["-i", resumed ? AgentBriefing.ResumePrompt : AgentBriefing.LaunchPrompt]);
        return args;
    }

    /// <summary>The shim's file name and text: a bash script on Unix, a batch file on Windows.</summary>
    public static (string Name, string Text) Shim(IReadOnlyList<string> self, bool windows) => windows
        ? ("milligram.cmd", $"@echo off\r\n{string.Join(' ', self.Select(BatchQuote))} %*\r\n")
        : ("milligram", $"#!/usr/bin/env bash\nexec {string.Join(' ', self.Select(BashQuote))} \"$@\"\n");

    public static string BashQuote(string word) => "'" + word.Replace("'", "'\\''") + "'";

    /// <summary>Windows paths can't hold a quote; inside a batch file, % is doubled so it isn't expanded.</summary>
    private static string BatchQuote(string word) => "\"" + word.Replace("%", "%%") + "\"";

    private (AgentConversation Conversation, bool Resumed) LoadOrCreate()
    {
        if (JsonFile.Read<AgentConversation>(paths.AgentStateFile) is { SessionId.Length: > 0 } existing) return (existing, true);
        var created = new AgentConversation(Guid.NewGuid().ToString(), DateTimeOffset.UtcNow);
        JsonFile.Write(paths.AgentStateFile, created);
        return (created, false);
    }

    private string WriteShim(bool windows)
    {
        var directory = Path.Combine(paths.RunDirectory, "bin");
        Directory.CreateDirectory(directory);
        var (name, text) = Shim(self, windows);
        var shim = Path.Combine(directory, name);
        File.WriteAllText(shim, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(shim, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                       UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return directory;
    }
}
