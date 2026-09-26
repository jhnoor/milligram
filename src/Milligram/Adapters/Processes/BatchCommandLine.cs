using System.Text;

namespace Milligram.Adapters.Processes;

/// <summary>
/// Builds the command line that runs a .cmd or .bat file through cmd.exe without letting an argument break out.
/// cmd.exe expands %variables% and obeys &amp;, |, &lt; and &gt; outside quotes, whatever .NET escapes (the "BatBadBut"
/// class of bugs). The quoting follows the Rust standard library's fix for the same bug.
/// </summary>
public static class BatchCommandLine
{
    /// <summary>Arguments for cmd.exe: no AutoRun (/d), no !delayed! expansion (/v:OFF), and the whole command quoted once (/s /c "…").</summary>
    public static string For(string batchFile, IReadOnlyList<string> args)
    {
        var line = new StringBuilder("/d /e:ON /v:OFF /s /c \"");
        Append(line, batchFile, quote: true);
        foreach (var arg in args) Append(line.Append(' '), arg, quote: false);
        return line.Append('"').ToString();
    }

    /// <summary>Printable ASCII that is safe outside quotes. Everything else, and anything unknown, is quoted.</summary>
    private const string Unquoted = @"#$*+-./:?@\_";

    private static void Append(StringBuilder line, string arg, bool quote)
    {
        if (arg.IndexOfAny(['\0', '\r', '\n']) >= 0)
            throw new ArgumentException("A .cmd or .bat file can't be given an argument that contains a line break.", nameof(arg));
        // A trailing backslash is quoted too, so a script's "%~1" can't have it escape the closing quote.
        quote |= arg.Length == 0 || arg.EndsWith('\\') || arg.Any(NeedsQuotes);
        if (quote) line.Append('"');
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                line.Append(c);
                continue;
            }
            // Backslashes before a quote are doubled, and the quote is doubled, so the program sees one of each.
            if (c == '"') line.Append('\\', backslashes).Append('"');
            // %cd:~,% expands to nothing, and splits any %name% so that cmd.exe can't expand it.
            else if (c == '%') line.Append("%%cd:~,");
            backslashes = 0;
            line.Append(c);
        }
        if (quote) line.Append('\\', backslashes).Append('"');
    }

    private static bool NeedsQuotes(char c) =>
        char.IsAscii(c) ? !(char.IsAsciiLetterOrDigit(c) || Unquoted.Contains(c)) : char.IsControl(c);
}
