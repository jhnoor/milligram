namespace Milligram.Adapters.Processes;

/// <summary>
/// Finds a program the way a shell does, as a pure function of the command, PATH, PATHEXT and the OS, so every
/// OS's rules are tested on every OS. Starting the path it returns runs exactly the program that was found.
/// </summary>
public static class ProgramPath
{
    public const string DefaultPathExt = ".COM;.EXE;.BAT;.CMD";

    /// <summary>The full path of <paramref name="command"/>, or null when no candidate <paramref name="exists"/>.</summary>
    public static string? Resolve(string command, string? path, string? pathExt, bool windows, Func<string, bool> exists)
    {
        if (command.Length == 0) return null;
        var extensions = windows ? Extensions(pathExt) : [];
        var candidates = HasDirectory(command, windows)
            ? Candidates(command, extensions, windows)
            : Directories(path, windows).SelectMany(directory => Candidates(Join(directory, command, windows), extensions, windows));
        return candidates.FirstOrDefault(exists);
    }

    /// <summary>Windows runs .cmd and .bat files through cmd.exe, which parses their arguments by its own rules.</summary>
    public static bool IsBatchFile(string program) =>
        program.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || program.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// On Windows, a name is tried as given only when it has an extension, then with each of PATHEXT. An extensionless
    /// file can't be started there: npm, for one, puts a bash script called `copilot` next to `copilot.cmd`.
    /// </summary>
    private static IEnumerable<string> Candidates(string file, IReadOnlyList<string> extensions, bool windows)
    {
        if (!windows || HasExtension(file)) yield return file;
        foreach (var extension in extensions) yield return file + extension;
    }

    private static IReadOnlyList<string> Extensions(string? pathExt) =>
        (string.IsNullOrWhiteSpace(pathExt) ? DefaultPathExt : pathExt)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.StartsWith('.') ? e : "." + e)
            .ToList();

    private static IEnumerable<string> Directories(string? path, bool windows) =>
        (path ?? "")
            .Split(windows ? ';' : ':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => windows ? d.Trim('"') : d)
            .Where(d => d.Length > 0);

    private static bool HasDirectory(string command, bool windows) =>
        windows ? command.IndexOfAny(['\\', '/', ':']) >= 0 : command.Contains('/');

    private static bool HasExtension(string file)
    {
        var name = file[(file.LastIndexOfAny(['\\', '/']) + 1)..];
        return name.LastIndexOf('.') > 0;
    }

    private static string Join(string directory, string command, bool windows)
    {
        var separator = windows ? '\\' : '/';
        return directory.TrimEnd(separator, '/') + separator + command;
    }
}
