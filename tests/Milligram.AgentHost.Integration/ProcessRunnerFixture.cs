using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Milligram.Adapters.Processes;
using Milligram.Application;

namespace Milligram.AgentHost.Integration;

/// <summary>Real inherited pipes reproduce jobs whose parent has exited while a descendant still owns stdout and stderr.</summary>
internal static class ProcessRunnerFixture
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static readonly string AssemblyPath = typeof(Program).Assembly.Location;
    private static readonly string Stdout = string.Concat(Enumerable.Range(0, 2000).Select(i => $"OUT:{i}\n")) + "\u001b[31mOUT:tail 漢 🐱\u001b[0m   ";
    private static readonly string Stderr = string.Concat(Enumerable.Range(0, 2000).Select(i => $"ERR:{i}\n")) + "ERR:tail";

    public static async Task Run()
    {
        var lines = new ConcurrentQueue<string>();
        var code = await new ProcessRunner().RunAsync(Program.Dotnet(), Args("normal"), Environment.CurrentDirectory,
            lines.Enqueue, CancellationToken.None).WaitAsync(Deadline);
        Require(code == 17 && lines.Count == 4002, "Streaming lost an exit code or final output.");
        Require(lines.Contains("OUT:tail 漢 🐱") && lines.Contains("ERR:tail"), "Streaming lost or failed to clean final unterminated lines.");
        var capture = await Task.Run(() => ProcessRunner.Capture(Program.Dotnet(), Args("normal"))).WaitAsync(Deadline);
        Require(capture.ExitCode == 17 && capture.Output == Stdout + Stderr, "Capture changed raw output or its exit code.");
        await HeldPipes(capture: false, cancel: false);
        await HeldPipes(capture: true, cancel: false);
        await HeldPipes(capture: false, cancel: true);
        await CancelRunning();
        await CaptureTimeout();
        await OutputBeforeExit(callbackFailure: false);
        await OutputBeforeExit(callbackFailure: true);
        await FollowedExit(ignoreInput: false);
        await FollowedExit(ignoreInput: true);
        Console.WriteLine("PASS: redirected output drains, inherited pipes are bounded, cancellation joins readers and preserves owned descendants");
    }

    public static async Task<int> Child(string[] args)
    {
        if (args[0] == "normal")
        {
            Console.Out.Write(Stdout);
            Console.Error.Write(Stderr);
            return 17;
        }
        var root = args[1];
        JsonFile.Write(Path.Combine(root, args[0] == "descendant" ? "descendant.json" : "parent.json"), new Identity(Environment.ProcessId));
        if (args[0] is "follow" or "ignore-input")
        {
            if (args[0] == "ignore-input")
            {
                if (!ProcessRunner.Launch(Program.Dotnet(), Args("descendant", root), root)) return 1;
                await Marker(root, "descendant.json");
            }
            Console.WriteLine("followed ready");
            if (args[0] == "ignore-input") await Marker(root, "release");
            else
            {
                await Console.In.ReadLineAsync();
                Console.Error.WriteLine("input closed");
            }
            return 17;
        }
        if (args[0] == "hold")
        {
            if (!ProcessRunner.Launch(Program.Dotnet(), Args("descendant", root), root)) return 1;
            Console.WriteLine("parent ready");
            await Marker(root, "exit-parent");
            return 17;
        }
        if (args[0] == "descendant")
        {
            Console.Error.WriteLine("descendant ready");
            await Marker(root, "release");
            try { Console.WriteLine("late stdout"); Console.Error.WriteLine("late stderr"); }
            catch (IOException) { }
            return 0;
        }
        if (args[0] is "closed" or "callback")
        {
            await Marker(root, "trigger");
            if (args[0] == "closed")
            {
                if (OperatingSystem.IsWindows())
                {
                    Require(CloseHandle(GetStdHandle(-11)), "Could not close stdout.");
                    Require(CloseHandle(GetStdHandle(-12)), "Could not close stderr.");
                }
                else { Require(Close(1) == 0 && Close(2) == 0, "Could not close output descriptors."); }
                await File.WriteAllTextAsync(Path.Combine(root, "closed"), "closed");
            }
            else Console.WriteLine("fail callback");
            await Marker(root, "release");
            return 17;
        }
        await Marker(root, "release");
        return 0;
    }

    private static async Task FollowedExit(bool ignoreInput)
    {
        var root = TemporaryRoot();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var followed = ProcessRunner.Follow(Program.Dotnet(), Args(ignoreInput ? "ignore-input" : "follow", root),
            line => { if (line == "followed ready") ready.TrySetResult(); }, line => errors.TrySetResult(line))
            ?? throw new InvalidOperationException("Could not start the followed child.");
        Process? child = null;
        Process? descendant = null;
        try
        {
            await ready.Task.WaitAsync(Deadline);
            child = await ChildProcess(root, "parent.json");
            if (ignoreInput) descendant = await ChildProcess(root, "descendant.json");
            var graceful = followed.Stop(out var diagnostic);
            Require(graceful != ignoreInput, diagnostic);
            await child.WaitForExitAsync().WaitAsync(Deadline);
            if (descendant is not null) await descendant.WaitForExitAsync().WaitAsync(Deadline);
            if (ignoreInput) Require(diagnostic.Contains("2000 ms exit grace period elapsed", StringComparison.Ordinal), diagnostic);
            else
            {
                Require(diagnostic.EndsWith("(code 17).", StringComparison.Ordinal), diagnostic);
                Require(await errors.Task.WaitAsync(Deadline) == "input closed", "Lost the followed child's stderr.");
            }
        }
        finally
        {
            followed.Dispose();
            await Finish(child);
            await Finish(descendant);
            Delete(root);
        }
    }

    private static async Task HeldPipes(bool capture, bool cancel)
    {
        var root = TemporaryRoot();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var lines = new ConcurrentQueue<string>();
        Process? parent = null;
        Process? descendant = null;
        Task<int>? streaming = null;
        Task<(int ExitCode, string Output)>? captured = null;
        try
        {
            if (capture) captured = Task.Run(() => ProcessRunner.Capture(Program.Dotnet(), Args("hold", root)));
            else streaming = new ProcessRunner().RunAsync(Program.Dotnet(), Args("hold", root), root, lines.Enqueue, cancellation.Token);
            parent = await ChildProcess(root, "parent.json");
            descendant = await ChildProcess(root, "descendant.json");
            await File.WriteAllTextAsync(Path.Combine(root, "exit-parent"), "exit");
            await parent.WaitForExitAsync().WaitAsync(Deadline);
            if (cancel)
            {
                cancellation.Cancel();
                try { await streaming!.WaitAsync(Deadline); throw new InvalidOperationException("Cancellation during drain was ignored."); }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            }
            else if (capture)
            {
                var result = await captured!.WaitAsync(Deadline);
                Require(result.ExitCode == 17 && result.Output.Contains("parent ready", StringComparison.Ordinal)
                    && result.Output.Contains("descendant ready", StringComparison.Ordinal)
                    && result.Output.Contains("Stopped waiting for redirected output", StringComparison.Ordinal), "Capture lost output, the exit code or the drain diagnostic.");
            }
            else
            {
                Require(await streaming!.WaitAsync(Deadline) == 17, "Inherited pipes changed the exit code.");
                Require(lines.Contains("parent ready") && lines.Contains("descendant ready")
                    && lines.Any(line => line.StartsWith("Stopped waiting for redirected output", StringComparison.Ordinal)), "Streaming lost output or the drain diagnostic.");
            }
            Require(!descendant.HasExited, "Finishing the command killed a surviving editor-like descendant.");
            var count = lines.Count;
            await File.WriteAllTextAsync(Path.Combine(root, "release"), "release");
            await descendant.WaitForExitAsync().WaitAsync(Deadline);
            Require(lines.Count == count, "A reader delivered output after RunAsync returned.");
        }
        finally
        {
            cancellation.Cancel();
            await File.WriteAllTextAsync(Path.Combine(root, "release"), "release");
            await Finish(parent);
            await Finish(descendant);
            if (streaming is not null) await Observe(streaming);
            if (captured is not null) await Observe(captured);
            Delete(root);
        }
    }

    private static async Task CancelRunning()
    {
        var root = TemporaryRoot();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var running = new ProcessRunner().RunAsync(Program.Dotnet(), Args("wait", root), root, _ => { }, cancellation.Token);
        Process? parent = null;
        try
        {
            parent = await ChildProcess(root, "parent.json");
            cancellation.Cancel();
            try { await running.WaitAsync(Deadline); throw new InvalidOperationException("Running command ignored cancellation."); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            Require(parent.HasExited, "Cancellation left the owned command running.");
        }
        finally
        {
            cancellation.Cancel();
            await File.WriteAllTextAsync(Path.Combine(root, "release"), "release");
            await Finish(parent);
            await Observe(running);
            Delete(root);
        }
    }

    private static string[] Args(string mode, string? root = null) => root is null
        ? [AssemblyPath, "--process-child", mode]
        : [AssemblyPath, "--process-child", mode, root];

    private static async Task CaptureTimeout()
    {
        var root = TemporaryRoot();
        var capturing = Task.Run(() => ProcessRunner.Capture(Program.Dotnet(), Args("wait", root)));
        Process? parent = null;
        try
        {
            parent = await ChildProcess(root, "parent.json");
            var result = await capturing.WaitAsync(TimeSpan.FromSeconds(20));
            Require(result == (124, "timed out"), "Capture lost its timeout result.");
            Require(parent.HasExited, "Capture timed out without stopping its owned command.");
        }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(root, "release"), "release");
            await Finish(parent);
            await Observe(capturing);
            Delete(root);
        }
    }

    private static async Task OutputBeforeExit(bool callbackFailure)
    {
        var root = TemporaryRoot();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var failure = new IOException("callback failed");
        var running = new ProcessRunner().RunAsync(Program.Dotnet(), Args(callbackFailure ? "callback" : "closed", root),
            root, _ => throw failure, cancellation.Token);
        Process? parent = null;
        try
        {
            parent = await ChildProcess(root, "parent.json");
            await File.WriteAllTextAsync(Path.Combine(root, "trigger"), "trigger");
            if (callbackFailure)
            {
                try { await running.WaitAsync(Deadline); throw new InvalidOperationException("The callback failure was lost."); }
                catch (IOException error) when (ReferenceEquals(error, failure)) { }
                Require(parent.HasExited, "The callback failure left its command running.");
            }
            else
            {
                await Marker(root, "closed");
                await Task.Delay(100);
                Require(!running.IsCompleted, "Closing output ended the command before its process exited.");
                await File.WriteAllTextAsync(Path.Combine(root, "release"), "release");
                Require(await running.WaitAsync(Deadline) == 17, "An output EOF lost the eventual exit code.");
            }
        }
        finally
        {
            cancellation.Cancel();
            await File.WriteAllTextAsync(Path.Combine(root, "release"), "release");
            await Finish(parent);
            try { await Observe(running); }
            catch (IOException error) when (ReferenceEquals(error, failure)) { }
            Delete(root);
        }
    }

    private static async Task<Process> ChildProcess(string root, string file)
    {
        using var deadline = new CancellationTokenSource(Deadline);
        Identity? identity;
        while ((identity = JsonFile.Read<Identity>(Path.Combine(root, file))) is null) await Task.Delay(20, deadline.Token);
        var process = Process.GetProcessById(identity.Pid);
        _ = process.SafeHandle;
        return process;
    }

    private static async Task Marker(string root, string file)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        while (!File.Exists(Path.Combine(root, file))) await Task.Delay(20, deadline.Token);
    }

    private static async Task Finish(Process? process)
    {
        if (process is null) return;
        using (process)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(Deadline);
        }
    }

    private static async Task Observe(Task task)
    {
        try { await task.WaitAsync(Deadline); }
        catch (OperationCanceledException) { }
    }

    private static string TemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "Milligram pipes " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Delete(string root)
    {
        var temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (Path.GetFullPath(root).StartsWith(temporary, StringComparison.Ordinal)) Directory.Delete(root, recursive: true);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed record Identity(int Pid);

    [DllImport("kernel32.dll")]
    private static extern nint GetStdHandle(int standardHandle);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
    [DllImport("libc", EntryPoint = "close")]
    private static extern int Close(int descriptor);
}
