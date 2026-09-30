using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Milligram.Application;

namespace Milligram.Adapters.Files;

/// <summary>Agent identity follows the actual directory, including aliases in its ancestors and filesystem casing.</summary>
internal static class PhysicalProjectRoot
{
    public static string Resolve(string root)
    {
        var full = Path.GetFullPath(root);
        if (!Directory.Exists(full)) throw new MilligramException($"Project directory does not exist or is inaccessible: {full}");
        return Path.TrimEndingDirectorySeparator(OperatingSystem.IsWindows() ? Windows(full) : Unix(full));
    }

    private static string Windows(string path)
    {
        using var handle = CreateFile(path, 0, 7, 0, 3, 0x02000000, 0);
        if (handle.IsInvalid) throw Failure(path);
        var buffer = new StringBuilder(32768);
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity) throw Failure(path);
        return buffer.ToString();
    }

    private static string Unix(string path)
    {
        var resolved = RealPath(path, 0);
        if (resolved == 0) throw Failure(path);
        try { return Marshal.PtrToStringUTF8(resolved)!; }
        finally { Free(resolved); }
    }

    private static MilligramException Failure(string path) =>
        new($"Could not resolve project directory '{path}': {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint capacity, uint flags);

    [DllImport("libc", EntryPoint = "realpath", SetLastError = true)]
    private static extern nint RealPath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, nint resolved);

    [DllImport("libc", EntryPoint = "free")]
    private static extern void Free(nint memory);
}
