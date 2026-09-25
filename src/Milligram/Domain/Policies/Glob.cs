using System.Text.RegularExpressions;

namespace Milligram.Domain.Policies;

/// <summary>Minimal globs over '/'-separated relative paths: **, *, and ?.</summary>
public static class Glob
{
    public static Regex ToRegex(string glob)
    {
        var pattern = new System.Text.StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                var slash = i + 2 < glob.Length && glob[i + 2] == '/';
                pattern.Append(slash ? "(?:.*/)?" : ".*");
                i += slash ? 2 : 1;
            }
            else if (c == '*') pattern.Append("[^/]*");
            else if (c == '?') pattern.Append("[^/]");
            else pattern.Append(Regex.Escape(c.ToString()));
        }
        return new Regex(pattern.Append('$').ToString(), RegexOptions.CultureInvariant);
    }

    public static bool Matches(string glob, string path) => ToRegex(glob).IsMatch(path);
}
