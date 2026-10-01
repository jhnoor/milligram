using System.ComponentModel;
using System.Runtime.InteropServices;
using Milligram.Application;

namespace Milligram.Adapters.Files;

/// <summary>Unix link publishes a complete sibling without File.Move's check-then-rename race.</summary>
public sealed class NewFilePublisher : INewFilePublisher
{
    public bool TryPublish(string completedFile, string destination)
    {
        if (OperatingSystem.IsWindows())
        {
            try { File.Move(completedFile, destination); return true; }
            catch (Exception e) when (File.Exists(destination) && e is IOException or UnauthorizedAccessException) { return false; }
        }

        if (Link(completedFile, destination) == 0) return true;
        var error = Marshal.GetLastPInvokeError();
        if (File.Exists(destination)) return false;
        throw new IOException($"Could not publish {destination}: {new Win32Exception(error).Message}");
    }

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link([MarshalAs(UnmanagedType.LPUTF8Str)] string completedFile, [MarshalAs(UnmanagedType.LPUTF8Str)] string destination);
}
