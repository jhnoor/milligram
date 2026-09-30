using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Milligram.Application;

namespace Milligram.Adapters.Processes;

public sealed partial class ProcessRunner
{
    private static readonly Lock detachedStart = new();

    /// <summary>The caller owns the returned process handle; the host owns its log and outlives that handle.</summary>
    public static IDetachedProcess? StartDetached(string command, IReadOnlyList<string> args, string workingDirectory)
    {
        try
        {
            lock (detachedStart)
            {
                using var handles = OperatingSystem.IsWindows() ? new DetachedStandardHandles() : null;
                var process = Process.Start(DetachedStartInfo(command, args, workingDirectory));
                return process is null ? null : new DetachedProcess(process);
            }
        }
        catch (Exception error) when (error is Win32Exception or ArgumentException) { return null; }
    }

    internal static ProcessStartInfo DetachedStartInfo(string command, IReadOnlyList<string> args, string workingDirectory)
    {
        var info = StartInfo(command, args, workingDirectory, redirect: true);
        info.RedirectStandardInput = true;
        info.CreateNoWindow = true;
        info.WindowStyle = ProcessWindowStyle.Hidden;
        return info;
    }

    /// <summary>A background Unix host gets its own session before spawning the agent, so viewer Ctrl+C cannot reach it.</summary>
    public static void DetachHostSession()
    {
        if (!OperatingSystem.IsWindows() && CreateSession() < 0)
            throw new MilligramException("Could not detach the agent host. Start it with `milligram agent start`: " +
                new Win32Exception(Marshal.GetLastPInvokeError()).Message);
    }

    [DllImport("libc", EntryPoint = "setsid", SetLastError = true)]
    private static extern int CreateSession();

    private sealed class DetachedProcess(Process process) : IDetachedProcess
    {
        public int Id => process.Id;
        public bool HasExited => process.HasExited;
        public int ExitCode => process.ExitCode;
        public void Stop()
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception && process.HasExited) { }
        }
        public void Dispose() => process.Dispose();
    }

    /// <summary>CreateProcess inherits other inheritable handles even when all three standard streams are redirected.</summary>
    private sealed class DetachedStandardHandles : IDisposable
    {
        private readonly List<(nint Handle, uint Flags)> changed = [];

        public DetachedStandardHandles()
        {
            try
            {
                foreach (var kind in new[] { -10, -11, -12 })
                {
                    var handle = GetStdHandle(kind);
                    if (handle == 0 || handle == -1) continue;
                    if (!GetHandleInformation(handle, out var flags)) throw new Win32Exception(Marshal.GetLastPInvokeError());
                    if ((flags & 1) == 0) continue;
                    if (!SetHandleInformation(handle, 1, 0)) throw new Win32Exception(Marshal.GetLastPInvokeError());
                    changed.Add((handle, flags));
                }
            }
            catch { Dispose(); throw; }
        }

        public void Dispose()
        {
            foreach (var (handle, flags) in changed)
                if (!SetHandleInformation(handle, 1, flags & 1)) throw new Win32Exception(Marshal.GetLastPInvokeError());
            changed.Clear();
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern nint GetStdHandle(int kind);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetHandleInformation(nint handle, out uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetHandleInformation(nint handle, uint mask, uint flags);
    }
}
