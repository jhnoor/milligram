using Milligram.Application;

namespace Milligram.Adapters.Files;

/// <summary>
/// A Windows drive seen from WSL (DrvFs, served over 9P under WSL2). Linux gets no inotify events there for changes
/// that Windows programs make (microsoft/WSL#4739), so the project watcher also watches such a project from Windows.
/// </summary>
public sealed class DrvFs : IWatchLimits
{
    private const string Mounts = "/proc/mounts";

    public WatchLimit? For(string root) => IsWindowsDrive(root)
        ? new WatchLimit(
            "the project is on a Windows drive, where Linux isn't told about changes that Windows programs make",
            "for faster scans and instant updates, keep the project in the Linux file system (for example under ~/src)")
        : null;

    public static bool IsWindowsDrive(string path)
    {
        if (!OperatingSystem.IsLinux()) return false;
        try { return Holds(File.ReadAllText(Mounts), path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Whether the mount that holds <paramref name="path"/> is DrvFs: the last, longest mount point above it in /proc/mounts.</summary>
    public static bool Holds(string mounts, string path)
    {
        var longest = -1;
        var drvfs = false;
        foreach (var line in mounts.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split(' ');
            if (fields.Length < 4) continue;
            var point = Unescape(fields[1]);
            if (point.Length < longest || !Under(path, point)) continue;
            longest = point.Length;
            drvfs = fields[2] == "drvfs" || fields[2] == "9p" && fields[3].Split(',', ';').Contains("aname=drvfs");
        }
        return drvfs;
    }

    private static bool Under(string path, string point) =>
        point == "/" || path == point || path.StartsWith(point.TrimEnd('/') + "/", StringComparison.Ordinal);

    /// <summary>/proc/mounts writes a space, tab, newline or backslash in a path as a three-digit octal escape.</summary>
    private static string Unescape(string field)
    {
        var text = new System.Text.StringBuilder(field.Length);
        for (var i = 0; i < field.Length; i++)
        {
            if (field[i] == '\\' && IsOctal(field, i + 1))
            {
                text.Append((char)Convert.ToInt32(field.Substring(i + 1, 3), 8));
                i += 3;
            }
            else text.Append(field[i]);
        }
        return text.ToString();
    }

    private static bool IsOctal(string field, int start) =>
        start + 3 <= field.Length && field.AsSpan(start, 3).IndexOfAnyExceptInRange('0', '7') < 0;
}
