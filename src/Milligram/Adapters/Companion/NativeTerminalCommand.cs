using Milligram.Application;

namespace Milligram.Adapters.Companion;

/// <summary>Expands an attachment as separate arguments, preserving this build and the selected project.</summary>
internal sealed record NativeTerminalCommand(string Command, IReadOnlyList<string> Args)
{
    public static NativeTerminalCommand? Parse(string? template, IReadOnlyList<string> self, string root)
    {
        if (template is "auto" or "none") return null;
        const string invalid = "agent.terminal must be 'auto', 'none', or a terminal command ending in one separate {command} argument (for example: wt.exe new-tab {command}). Use {command} instead of tmux's {session}.";
        if (string.IsNullOrWhiteSpace(template) || template.Count(c => c == '"') % 2 != 0) throw new MilligramException(invalid);
        var words = Desktop.Split(template);
        if (words.Count < 2 || string.IsNullOrWhiteSpace(words[0]) || words[^1] != "{command}" ||
            words.Take(words.Count - 1).Any(w => w.Contains("{command}", StringComparison.Ordinal) || w.Contains("{session}", StringComparison.Ordinal)))
            throw new MilligramException(invalid);
        IEnumerable<string> attach = [.. self, "agent", "attach", "--project", root];
        var name = Path.GetFileNameWithoutExtension(words[0].Replace('\\', '/').Split('/')[^1]);
        // Windows Terminal splits even quoted arguments at semicolons; its own escape is a backslash.
        if (string.Equals(name, "wt", StringComparison.OrdinalIgnoreCase)) attach = attach.Select(a => a.Replace(";", "\\;", StringComparison.Ordinal));
        return new(words[0], [.. words.Skip(1).SkipLast(1), .. attach]);
    }
}
