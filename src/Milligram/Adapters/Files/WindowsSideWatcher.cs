using System.Text;
using Milligram.Adapters.Processes;

namespace Milligram.Adapters.Files;

/// <summary>
/// Watches a project on a Windows drive from the Windows side, for Milligram running in WSL. powershell.exe runs a
/// FileSystemWatcher there, which sees every change on the drive whoever makes it, and prints each changed path.
/// Unlike polling over 9P, its cost doesn't grow with the size of the project.
/// </summary>
public sealed class WindowsSideWatcher : IDisposable
{
    public const string Ready = "?ready";
    public const string Overflow = "?overflow";

    /// <summary>
    /// The directories that are never scanned, and anything in them, for PowerShell's case-insensitive -match. The
    /// directory itself counts too: writing a file inside it can raise a change for the directory, with no trailing \.
    /// </summary>
    public const string Unscanned = @"\\(bin|obj|\.git|node_modules|\.vs|\.idea|\.milligram\\run)(\\|$)";

    private const string PowerShellOnC = "/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe";

    private readonly ProcessRunner.Followed process;

    private WindowsSideWatcher(ProcessRunner.Followed process) => this.process = process;

    /// <summary>Null when WSL can't start powershell.exe (interop turned off) or the watcher doesn't start in time.</summary>
    public static WindowsSideWatcher? Start(string root, Action<string> changed, Action overflowed, TimeSpan wait)
    {
        if (WindowsPath(root) is not { } windowsRoot || PowerShell() is not { } powershell) return null;
        // Not disposed: a late "?ready" may still arrive after a timeout, and it holds no wait handle unless waited on long.
        var ready = new ManualResetEventSlim();
        var process = ProcessRunner.Follow(powershell,
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Encode(Script(windowsRoot))],
            line =>
            {
                if (line == Ready) ready.Set();
                else if (line == Overflow) overflowed();
                else if (ToLinux(line, windowsRoot, root) is { } path) changed(path);
            });
        if (process is null) return null;
        if (ready.Wait(wait)) return new WindowsSideWatcher(process);
        process.Dispose();
        return null;
    }

    /// <summary>The Linux path of a Windows path under the project, or null for a path outside it.</summary>
    public static string? ToLinux(string windowsPath, string windowsRoot, string linuxRoot)
    {
        var root = windowsRoot.TrimEnd('\\');
        var linux = linuxRoot.TrimEnd('/');
        if (!windowsPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
        if (windowsPath.Length == root.Length) return linux;
        if (windowsPath[root.Length] != '\\') return null;
        return linux + windowsPath[root.Length..].Replace('\\', '/');
    }

    /// <summary>
    /// Prints "?ready" once watching, then each changed path under the root except in directories that are never scanned,
    /// and "?overflow" when changes came too fast to list. It stops when its input closes, that is, when Milligram exits.
    /// </summary>
    public static string Script(string windowsRoot) => $$"""
        $ErrorActionPreference = 'Stop'
        $out = New-Object IO.StreamWriter -ArgumentList ([Console]::OpenStandardOutput()), (New-Object Text.UTF8Encoding -ArgumentList $false)
        $out.AutoFlush = $true
        $watcher = New-Object IO.FileSystemWatcher -ArgumentList '{{windowsRoot.Replace("'", "''")}}'
        $watcher.IncludeSubdirectories = $true
        $watcher.InternalBufferSize = 65536
        $watcher.NotifyFilter = [IO.NotifyFilters]'FileName, DirectoryName, LastWrite, Size'
        foreach ($name in 'Changed', 'Created', 'Deleted', 'Renamed', 'Error') { $null = Register-ObjectEvent -InputObject $watcher -EventName $name -SourceIdentifier $name }
        $watcher.EnableRaisingEvents = $true
        $out.WriteLine('{{Ready}}')
        $eof = [Console]::OpenStandardInput().ReadAsync((New-Object byte[] 1), 0, 1)
        while (-not $eof.IsCompleted) {
          $e = Wait-Event -Timeout 1
          if ($null -eq $e) { continue }
          Remove-Event -EventIdentifier $e.EventIdentifier
          if ($e.SourceIdentifier -eq 'Error') { $out.WriteLine('{{Overflow}}'); continue }
          $a = $e.SourceEventArgs
          $paths = if ($a -is [IO.RenamedEventArgs]) { $a.OldFullPath, $a.FullPath } else { $a.FullPath }
          foreach ($p in $paths) { if ($p -notmatch '{{Unscanned}}') { $out.WriteLine($p) } }
        }
        """;

    private static string Encode(string script) => Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    private static string? WindowsPath(string root)
    {
        var (exitCode, output) = ProcessRunner.Capture("wslpath", "-w", root);
        return exitCode == 0 && output.Trim() is { Length: > 0 } path ? path : null;
    }

    private static string? PowerShell() => ProcessRunner.Find("powershell.exe") ?? (File.Exists(PowerShellOnC) ? PowerShellOnC : null);

    public void Dispose() => process.Dispose();
}
