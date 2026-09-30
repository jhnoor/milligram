using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Milligram.Tests;

/// <summary>Real directory links for Windows tests without a shell or the symbolic-link privilege.</summary>
internal sealed class WindowsJunction(string path) : IDisposable
{
    public static WindowsJunction Create(string path, string target)
    {
        target = Path.GetFullPath(target);
        var substitute = Encoding.Unicode.GetBytes(@"\??\" + target);
        var display = Encoding.Unicode.GetBytes(target);
        using var data = new MemoryStream();
        using (var writer = new BinaryWriter(data, Encoding.Unicode, leaveOpen: true))
        {
            writer.Write(0xA0000003u);
            writer.Write(checked((ushort)(12 + substitute.Length + display.Length)));
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write(checked((ushort)substitute.Length));
            writer.Write(checked((ushort)(substitute.Length + 2)));
            writer.Write(checked((ushort)display.Length));
            writer.Write(substitute);
            writer.Write((ushort)0);
            writer.Write(display);
            writer.Write((ushort)0);
        }
        Directory.CreateDirectory(path);
        using var handle = CreateFile(path, 0x40000000, 7, 0, 3, 0x02200000, 0);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
        var buffer = data.ToArray();
        if (!DeviceIoControl(handle, 0x000900A4, buffer, buffer.Length, 0, 0, out _, 0))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        return new WindowsJunction(path);
    }

    public void Dispose() => Directory.Delete(path);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int length,
        nint output, int capacity, out int returned, nint overlapped);
}
