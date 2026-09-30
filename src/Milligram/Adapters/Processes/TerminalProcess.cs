using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Milligram.Adapters.Companion;
using Milligram.Application;
using Porta.Pty;

namespace Milligram.Adapters.Processes;

public sealed partial class ProcessRunner
{
    private static readonly Lazy<nint> conpty = new(PreloadConpty);

    /// <summary>Resolve and quote the agent exactly as other child processes, then give it an owned terminal.</summary>
    public static async Task<IAgentTerminal> StartTerminalAsync(AgentLaunch launch, TerminalSize size, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        HostProtocol.Resize(size);
        if (OperatingSystem.IsWindows()) _ = conpty.Value;
        var info = StartInfo(launch.Command, launch.Args, launch.WorkingDirectory, redirect: false);
        var locale = OperatingSystem.IsWindows() ? null : OperatingSystem.IsMacOS() ? "en_US.UTF-8" : "C.UTF-8";
        var asynchronous = !OperatingSystem.IsLinux() || Environment.OSVersion.Version >= new Version(5, 3);
        var options = TerminalOptions(info, launch, size, asynchronous, locale);
        var connection = await PtyProvider.SpawnAsync(options, cancellation);
        try
        {
            var terminal = new PtyTerminal(connection, StopTree(connection));
            try { cancellation.ThrowIfCancellationRequested(); return terminal; }
            catch { terminal.Dispose(); throw; }
        }
        catch { connection.Dispose(); throw; }
    }

    internal static PtyOptions TerminalOptions(ProcessStartInfo info, AgentLaunch launch, TerminalSize size, bool asynchronous, string? locale)
    {
        info.Environment.TryGetValue("PATH", out var inheritedPath);
        var options = new PtyOptions
        {
            Name = "Milligram agent",
            App = info.FileName,
            CommandLine = info.Arguments.Length > 0 ? [info.Arguments] : info.ArgumentList.ToArray(),
            VerbatimCommandLine = info.Arguments.Length > 0,
            Cwd = launch.WorkingDirectory,
            Cols = size.Columns,
            Rows = size.Rows,
            UseAsyncIo = asynchronous,
            Environment = new Dictionary<string, string>
            {
                ["PATH"] = launch.PathFor(inheritedPath, Path.PathSeparator),
                ["DOTNET_NOLOGO"] = "1",
                ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            },
        };
        if (locale is not null)
        {
            options.Environment["TERM"] = "xterm-256color";
            options.Environment["LANG"] = locale;
            options.Environment["LC_ALL"] = locale;
        }
        return options;
    }

    private static Action StopTree(IPtyConnection connection)
    {
        if (!OperatingSystem.IsWindows())
            return () =>
            {
                if (connection.Pid <= 0) throw new InvalidOperationException("The terminal has no owned process group.");
                if (Signal(-connection.Pid, 9) != 0 && Marshal.GetLastPInvokeError() != 3)
                    throw new IOException("Could not stop the terminal process group.", new Win32Exception(Marshal.GetLastPInvokeError()));
            };

        Process? owned = null;
        try
        {
            owned = Process.GetProcessById(connection.Pid);
            _ = owned.SafeHandle;
            if (connection.WaitForExit(0)) { owned.Dispose(); owned = null; }
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception && connection.WaitForExit(0))
        {
            owned?.Dispose();
            owned = null;
        }
        return () =>
        {
            try
            {
                if (owned is not null && !owned.HasExited) owned.Kill(entireProcessTree: true);
            }
            finally { owned?.Dispose(); }
        };
    }

    /// <summary>Keep a reference until process exit; ordinary DLL probing fails at a measured Windows path boundary.</summary>
    private static nint PreloadConpty()
    {
        if (!PtyProvider.PseudoConsoleImplementation.StartsWith("oob", StringComparison.Ordinal)) return 0;
        var path = Path.Combine(AppContext.BaseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native", "conpty.dll");
        return NativeLibrary.Load(ExtendedWindowsPath(path));
    }

    internal static string ExtendedWindowsPath(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path
        : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Signal(int pid, int signal);
}
