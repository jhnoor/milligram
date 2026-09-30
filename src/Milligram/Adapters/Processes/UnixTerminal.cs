using System.Runtime.InteropServices;
using Milligram.Adapters.Companion;

namespace Milligram.Adapters.Processes;

/// <summary>Libc owns the platform-specific termios layout; polling bounds cancellation without changing fd flags.</summary>
internal sealed class UnixTerminal : LocalTerminal
{
    private readonly byte[] saved = new byte[256];
    private readonly Stream output = Console.OpenStandardOutput();

    public UnixTerminal()
    {
        _ = Console.WindowWidth;
        if (GetAttributes(0, saved) != 0) throw NativeError("Read terminal modes");
        var raw = (byte[])saved.Clone();
        MakeRaw(raw);
        if (SetAttributes(0, 0, raw) != 0) throw NativeError("Set raw terminal modes");
    }

    public override TerminalSize Size => Dimensions(Console.WindowWidth, Console.WindowHeight);

    public override int Read(byte[] buffer, CancellationToken cancellation)
    {
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var descriptor = new PollDescriptor { File = 0, Events = 1 };
            var ready = Poll(ref descriptor, 1, 50);
            if (ready < 0)
            {
                if (Marshal.GetLastPInvokeError() == 4) continue;
                throw NativeError("Poll terminal input");
            }
            if (ready == 0) continue;
            if ((descriptor.Returned & 0x20) != 0) throw new IOException("The terminal input descriptor closed.");
            var read = ReadBytes(0, buffer, (nuint)buffer.Length);
            if (read >= 0) return checked((int)read);
            if (Marshal.GetLastPInvokeError() != 4) throw NativeError("Read terminal input");
        }
    }

    public override void Write(byte[] bytes) { output.Write(bytes); output.Flush(); }
    protected override void Restore()
    {
        try { if (SetAttributes(0, 0, saved) != 0) throw NativeError("Restore terminal modes"); }
        finally { output.Dispose(); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollDescriptor { public int File; public short Events; public short Returned; }
    [DllImport("libc", EntryPoint = "tcgetattr", SetLastError = true)]
    private static extern int GetAttributes(int descriptor, [Out] byte[] modes);
    [DllImport("libc", EntryPoint = "tcsetattr", SetLastError = true)]
    private static extern int SetAttributes(int descriptor, int actions, byte[] modes);
    [DllImport("libc", EntryPoint = "cfmakeraw")]
    private static extern void MakeRaw([In, Out] byte[] modes);
    [DllImport("libc", EntryPoint = "poll", SetLastError = true)]
    private static extern int Poll(ref PollDescriptor descriptor, nuint count, int timeout);
    [DllImport("libc", EntryPoint = "read", SetLastError = true)]
    private static extern nint ReadBytes(int descriptor, [Out] byte[] buffer, nuint count);
}
