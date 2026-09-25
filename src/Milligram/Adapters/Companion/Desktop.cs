using Milligram.Adapters.Processes;

namespace Milligram.Adapters.Companion;

/// <summary>Opens browsers, editors, and terminals on Linux, WSL, and macOS.</summary>
public static class Desktop
{
    public const string DefaultEditor = "code -g {file}:{line}";

    public static bool IsWsl => Environment.GetEnvironmentVariable("WSL_DISTRO_NAME") is { Length: > 0 };

    public static bool OpenUrl(string url)
    {
        if (OperatingSystem.IsMacOS()) return ProcessRunner.Launch("open", [url]);
        if (IsWsl)
        {
            if (ProcessRunner.OnPath("wslview")) return ProcessRunner.Launch("wslview", [url]);
            if (ProcessRunner.OnPath("explorer.exe")) return ProcessRunner.Launch("explorer.exe", [url]);
        }
        return ProcessRunner.OnPath("xdg-open") && ProcessRunner.Launch("xdg-open", [url]);
    }

    public static bool OpenEditor(string? template, string file, int line)
    {
        var words = Split(string.IsNullOrWhiteSpace(template) ? DefaultEditor : template)
            .Select(w => w.Replace("{file}", file).Replace("{line}", line.ToString()))
            .ToList();
        return words.Count > 0 && ProcessRunner.OnPath(words[0]) && ProcessRunner.Launch(words[0], words.Skip(1));
    }

    /// <summary>Opens a terminal window attached to a tmux session.</summary>
    public static bool OpenTerminal(string template, string session, string workingDirectory)
    {
        if (template == "none") return false;
        if (template != "auto")
        {
            var words = Split(template).Select(w => w.Replace("{session}", session)).ToList();
            return words.Count > 0 && ProcessRunner.Launch(words[0], words.Skip(1), workingDirectory);
        }
        string[] attach = ["tmux", "attach", "-t", session];
        if (IsWsl && ProcessRunner.OnPath("wt.exe"))
            return ProcessRunner.Launch("wt.exe",
                ["-w", "milligram", "new-tab", "--title", "Milligram agent", "wsl.exe", "-d", Environment.GetEnvironmentVariable("WSL_DISTRO_NAME")!, "--", .. attach],
                "/mnt/c");
        if (OperatingSystem.IsMacOS())
            return ProcessRunner.Launch("osascript", ["-e", $"tell application \"Terminal\" to do script \"tmux attach -t {session}\""]);
        foreach (var (terminal, flag) in new[] { ("x-terminal-emulator", "-e"), ("gnome-terminal", "--"), ("konsole", "-e"), ("xterm", "-e") })
            if (ProcessRunner.OnPath(terminal)) return ProcessRunner.Launch(terminal, [flag, .. attach], workingDirectory);
        return false;
    }

    /// <summary>Splits a command template on spaces, honouring double quotes.</summary>
    public static IReadOnlyList<string> Split(string command)
    {
        var words = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var c in command)
        {
            if (c == '"') quoted = !quoted;
            else if (c == ' ' && !quoted)
            {
                if (current.Length > 0) words.Add(current.ToString());
                current.Clear();
            }
            else current.Append(c);
        }
        if (current.Length > 0) words.Add(current.ToString());
        return words;
    }
}
