using System.ComponentModel;
using System.Runtime.InteropServices;
using Milligram.Adapters.Companion;
using Milligram.Application;

namespace Milligram.Adapters.Processes;

/// <summary>Owns console modes only while attached; native reads poll so detach can join the input reader.</summary>
public abstract class LocalTerminal : ILocalTerminal
{
    private readonly List<IDisposable> signals = [];
    private int disposed;
    public event Action? Resized;
    public event Action? Interrupted;

    public static ILocalTerminal Open()
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
            throw new MilligramException("Agent attachment needs an interactive terminal; input and output cannot be redirected.");
        LocalTerminal terminal = OperatingSystem.IsWindows() ? new WindowsTerminal() : new UnixTerminal();
        try { terminal.Watch(); return terminal; }
        catch { terminal.Dispose(); throw; }
    }

    public abstract TerminalSize Size { get; }
    public abstract int Read(byte[] buffer, CancellationToken cancellation);
    public abstract void Write(byte[] bytes);
    protected abstract void Restore();
    protected void Resize() => Resized?.Invoke();
    protected static TerminalSize Dimensions(int columns, int rows) => new(Math.Clamp(columns, 2, 1000), Math.Clamp(rows, 2, 1000));
    protected static IOException NativeError(string operation) => new(operation + ": " + new Win32Exception(Marshal.GetLastPInvokeError()).Message);

    private void Watch()
    {
        AppDomain.CurrentDomain.ProcessExit += OnExit;
        Console.CancelKeyPress += OnCancel;
        if (OperatingSystem.IsWindows()) return;
        signals.Add(PosixSignalRegistration.Create(PosixSignal.SIGWINCH, _ => Resize()));
        foreach (var signal in new[] { PosixSignal.SIGHUP, PosixSignal.SIGTERM, PosixSignal.SIGINT, PosixSignal.SIGQUIT })
            signals.Add(PosixSignalRegistration.Create(signal, context => { context.Cancel = true; Interrupted?.Invoke(); }));
    }

    private void OnCancel(object? sender, ConsoleCancelEventArgs args) { args.Cancel = true; Interrupted?.Invoke(); }
    private void OnExit(object? sender, EventArgs args) => Dispose();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try
        {
            try
            {
                try { Write("\u001b[?1049l\u001b[?2004l\u001b[?25h\u001b[0m"u8.ToArray()); }
                catch (IOException) { }
            }
            finally { Restore(); }
        }
        finally
        {
            Console.CancelKeyPress -= OnCancel;
            AppDomain.CurrentDomain.ProcessExit -= OnExit;
            foreach (var signal in signals) signal.Dispose();
        }
    }
}
