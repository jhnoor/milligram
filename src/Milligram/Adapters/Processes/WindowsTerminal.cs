using System.Runtime.InteropServices;
using System.Text;
using Milligram.Adapters.Companion;

namespace Milligram.Adapters.Processes;

/// <summary>VT input is queued as Unicode key records, so resize and cancellation need no blocked ReadFile.</summary>
internal sealed class WindowsTerminal : LocalTerminal
{
    private readonly nint input = GetStdHandle(-10);
    private readonly nint output = GetStdHandle(-11);
    private readonly uint inputMode;
    private readonly uint outputMode;
    private readonly Encoder encoder = Encoding.UTF8.GetEncoder();
    private readonly Decoder decoder = Encoding.UTF8.GetDecoder();
    private readonly TerminalInputMode inputModeFilter = new();
    private readonly Queue<char> pending = [];
    private readonly InputRecord[] records = new InputRecord[128];

    public WindowsTerminal()
    {
        if (!GetConsoleMode(input, out inputMode) || !GetConsoleMode(output, out outputMode)) throw NativeError("Read console modes");
        try
        {
            if (!SetConsoleMode(input, (inputMode & ~0x47u) | 0x288u) ||
                !SetConsoleMode(output, outputMode | 0xdu)) throw NativeError("Set raw console modes");
            Write("\u001b[?9001l"u8.ToArray());
        }
        catch { Restore(); throw; }
    }

    public override TerminalSize Size
    {
        get
        {
            if (!GetConsoleScreenBufferInfo(output, out var info)) throw NativeError("Read console size");
            return Dimensions(info.Right - info.Left + 1, info.Bottom - info.Top + 1);
        }
    }

    public override int Read(byte[] buffer, CancellationToken cancellation)
    {
        var characters = new char[Math.Min(128, buffer.Length / 3)];
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            if (pending.Count > 0)
            {
                var count = 0;
                while (pending.Count > 0 && count < characters.Length) characters[count++] = pending.Dequeue();
                var written = encoder.GetBytes(characters.AsSpan(0, count), buffer, flush: false);
                if (written > 0) return written;
            }
            var wait = WaitForSingleObject(input, 50);
            if (wait == 258) continue;
            if (wait != 0) throw NativeError("Wait for console input");
            if (!GetNumberOfConsoleInputEvents(input, out var available)) throw NativeError("Read console input count");
            if (available == 0) continue;
            if (!ReadConsoleInput(input, records, Math.Min(available, (uint)records.Length), out var read)) throw NativeError("Read console input");
            foreach (var record in records.AsSpan(0, (int)read))
            {
                if (record.Kind == 4) Resize();
                else if (record.Kind == 1 && record.KeyDown != 0 && (record.Character != '\0' ||
                    record.KeyCode is 0x20 or 0x32 && (record.Controls & 0xc) != 0))
                    for (var repeat = 0; repeat < record.Repeat; repeat++) pending.Enqueue(record.Character);
            }
        }
    }

    public override void Write(byte[] bytes)
    {
        var characters = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        var count = decoder.GetChars(bytes, characters, flush: false);
        var filtered = inputModeFilter.Feed(characters.AsSpan(0, count));
        if (filtered.Length > 0 && (!WriteConsole(output, filtered, (uint)filtered.Length, out var written, 0) || written != filtered.Length))
            throw NativeError("Write console output");
    }

    protected override void Restore()
    {
        var inputRestored = SetConsoleMode(input, inputMode);
        var outputRestored = SetConsoleMode(output, outputMode);
        if (!inputRestored || !outputRestored) throw NativeError("Restore console modes");
    }

    [StructLayout(LayoutKind.Explicit, Size = 20, CharSet = CharSet.Unicode)]
    private struct InputRecord
    {
        [FieldOffset(0)] public ushort Kind;
        [FieldOffset(4)] public int KeyDown;
        [FieldOffset(8)] public ushort Repeat;
        [FieldOffset(10)] public ushort KeyCode;
        [FieldOffset(14)] public char Character;
        [FieldOffset(16)] public uint Controls;
    }
    [StructLayout(LayoutKind.Explicit, Size = 22)]
    private struct BufferInfo
    {
        [FieldOffset(10)] public short Left;
        [FieldOffset(12)] public short Top;
        [FieldOffset(14)] public short Right;
        [FieldOffset(16)] public short Bottom;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GetStdHandle(int standard);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetConsoleMode(nint handle, out uint mode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetConsoleMode(nint handle, uint mode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNumberOfConsoleInputEvents(nint handle, out uint count);
    [DllImport("kernel32.dll", EntryPoint = "ReadConsoleInputW", SetLastError = true)]
    private static extern bool ReadConsoleInput(nint handle, [Out] InputRecord[] records, uint count, out uint read);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetConsoleScreenBufferInfo(nint handle, out BufferInfo info);
    [DllImport("kernel32.dll", EntryPoint = "WriteConsoleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WriteConsole(nint handle, char[] characters, uint count, out uint written, nint reserved);
}
