using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Porta.Pty;

namespace Milligram.PtySpike;

/// <summary>A throwaway native-terminal probe; no real agent or user commands are executed.</summary>
internal static class Program
{
    private const string Argument = "a \"quoted\" argument & 漢";
    private const string Unicode = "Grüße ☃ 漢字 🐱";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args.FirstOrDefault() == "--child") return await Child(args);
        var outputIndex = Array.IndexOf(args, "--output");
        var output = outputIndex >= 0 ? args[outputIndex + 1] : null;
        var asynchronous = !args.Contains("--blocking", StringComparer.Ordinal);
        var results = new List<string>
        {
            "# Porta.Pty 2.2.2 probe", "",
            $"Runtime: {RuntimeInformation.RuntimeIdentifier}; {RuntimeInformation.OSDescription}; {RuntimeInformation.FrameworkDescription}",
            $"Async I/O: {asynchronous}", "", "| Check | Result | Milliseconds |", "|---|---|---|",
        };
        var capture = new Capture();
        var fixture = Path.Combine(Path.GetTempPath(), "Milligram PTY probe " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        IPtyConnection? terminal = null;
        var stopReading = new CancellationTokenSource();
        Task? reading = null;
        var success = false;
        try
        {
            var expectedIndex = Array.IndexOf(args, "--expected-rid");
            if (expectedIndex >= 0)
                Require(RuntimeInformation.RuntimeIdentifier == args[expectedIndex + 1], "Runner used an unexpected runtime architecture");
            var runtime = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
            var dotnet = Path.Combine(runtime.Parent!.Parent!.Parent!.FullName, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            if (!File.Exists(dotnet)) throw new FileNotFoundException("Cannot locate the current .NET host", dotnet);
            var options = new PtyOptions
            {
                App = dotnet,
                CommandLine = [Assembly.GetExecutingAssembly().Location, "--child", Argument],
                Cwd = fixture,
                Cols = 80,
                Rows = 24,
                UseAsyncIo = asynchronous,
                Environment = new Dictionary<string, string>
                {
                    ["TERM"] = "xterm-256color",
                    ["LANG"] = OperatingSystem.IsMacOS() ? "en_US.UTF-8" : "C.UTF-8",
                    ["MILLIGRAM_PTY_CWD"] = fixture,
                    ["MILLIGRAM_PTY_VALUE"] = "value with spaces & Æ",
                },
            };
            terminal = await PtyProvider.SpawnAsync(options, CancellationToken.None).WaitAsync(Deadline);
            var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            terminal.ProcessExited += (_, e) => exited.TrySetResult(e.ExitCode);
            reading = capture.Read(terminal.ReaderStream, stopReading.Token);

            await Check("controlling terminal, arguments, environment and working directory", async () =>
            {
                await capture.Wait("READY");
                foreach (var marker in new[] { "TTY:True", "ARG:True", "CWD:True", "ENV:True", "SIZE:80x24" })
                    Require(capture.Text.Contains(marker, StringComparison.Ordinal), "Missing " + marker);
            });

            if (asynchronous)
                await Check("cancel idle read and resume the same terminal", async () =>
                {
                    Require(terminal.SupportsCancellableRead, "Async terminal does not report cancellable reads");
                    stopReading.Cancel();
                    await reading.WaitAsync(Deadline);
                    stopReading.Dispose();
                    stopReading = new CancellationTokenSource();
                    reading = capture.Read(terminal.ReaderStream, stopReading.Token);
                    await Send("resumed\r");
                    await capture.Wait("ACK:resumed");
                });

            await Check("UTF-8 across one-byte reads and writes", async () =>
            {
                await Send("unicode\r");
                await capture.Wait("UNICODE:" + Unicode);
                Require(!capture.Text.Contains('\uFFFD'), "UTF-8 replacement character appeared");
            });
            await Check("resize and SIGWINCH", async () =>
            {
                terminal.Resize(96, 31);
                await Send("size\r");
                await capture.Wait("SIZE:96x31");
                if (!OperatingSystem.IsWindows()) await capture.Wait("SIGWINCH");
            });
            await Check("Ctrl+C reaches the child without ending the session", async () =>
            {
                await Send("\u0003");
                await capture.Wait("INTERRUPTED");
                await Send("after-interrupt\r");
                await capture.Wait("ACK:after-interrupt");
            });
            await Check("doorbell line followed by carriage return", async () =>
            {
                await Send("milligram mail");
                await Task.Delay(100);
                Require(!capture.Text.Contains("ACK:milligram mail", StringComparison.Ordinal), "Line was submitted before carriage return");
                await Send("\r");
                await capture.Wait("ACK:milligram mail");
            });
            await Check("exit notification and nonzero exit code", async () =>
            {
                await Send("exit\r");
                Require(await exited.Task.WaitAsync(Deadline) == 17, "Exit notification lost code 17");
                Require(terminal.WaitForExit(1000) && terminal.ExitCode == 17, "WaitForExit disagrees with notification");
            });
            await Check("forced shutdown with a pending read", async () =>
            {
                using var stopped = await PtyProvider.SpawnAsync(options, CancellationToken.None).WaitAsync(Deadline);
                using var stop = new CancellationTokenSource();
                var stoppedOutput = new Capture();
                var pending = stoppedOutput.Read(stopped.ReaderStream, stop.Token);
                try
                {
                    await stoppedOutput.Wait("READY");
                    stopped.Kill();
                    Require(stopped.WaitForExit(10_000), "Killed terminal child did not exit");
                }
                finally
                {
                    if (!stopped.WaitForExit(0)) stopped.Kill();
                    stop.Cancel();
                    stopped.Dispose();
                    try { await pending.WaitAsync(Deadline); }
                    catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException) { }
                }
            });
            success = true;

            async Task Send(string text)
            {
                await terminal.WriterStream.WriteAsync(Encoding.UTF8.GetBytes(text)).AsTask().WaitAsync(Deadline);
                await terminal.WriterStream.FlushAsync().WaitAsync(Deadline);
            }

            async Task Check(string name, Func<Task> check)
            {
                var started = Stopwatch.GetTimestamp();
                try
                {
                    await check();
                    results.Add($"| {name} | PASS | {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} |");
                    Console.WriteLine("PASS " + name);
                }
                catch
                {
                    results.Add($"| {name} | FAIL | {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} |");
                    throw;
                }
            }
        }
        catch (Exception e)
        {
            results.AddRange(["", "Failure: " + e, "", "Terminal output:", "```text", capture.Text, "```"]);
            Console.Error.WriteLine(e);
            Console.Error.WriteLine(capture.Text);
        }
        finally
        {
            if (terminal is not null)
            {
                if (!terminal.WaitForExit(0)) terminal.Kill();
                terminal.WaitForExit(1000);
                stopReading.Cancel();
                terminal.Dispose();
            }
            if (reading is not null)
            {
                try { await reading.WaitAsync(Deadline); }
                catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException) { }
                catch (Exception e)
                {
                    success = false;
                    results.Add("\nReader cleanup failed: " + e.Message);
                    Console.Error.WriteLine(e);
                }
            }
            stopReading.Dispose();
            var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(fixture).StartsWith(temporaryRoot, StringComparison.Ordinal)) Directory.Delete(fixture, recursive: true);
            if (output is not null)
            {
                var file = Path.GetFullPath(output);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                await File.WriteAllTextAsync(file, string.Join('\n', results) + "\n");
            }
        }
        return success ? 0 : 1;
    }

    private static async Task<int> Child(string[] args)
    {
        Console.InputEncoding = new UTF8Encoding(false);
        Console.TreatControlCAsInput = true;
        using var resized = OperatingSystem.IsWindows() ? null
            : PosixSignalRegistration.Create(PosixSignal.SIGWINCH, context => { context.Cancel = true; Console.WriteLine("SIGWINCH"); });
        var attached = !Console.IsInputRedirected && !Console.IsOutputRedirected;
        if (!OperatingSystem.IsWindows())
        {
            using var controllingTerminal = File.Open("/dev/tty", FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            attached &= controllingTerminal.CanRead;
        }
        Console.WriteLine("TTY:" + attached);
        Console.WriteLine("ARG:" + (args.ElementAtOrDefault(1) == Argument));
        Console.WriteLine("CWD:" + (Environment.CurrentDirectory == Environment.GetEnvironmentVariable("MILLIGRAM_PTY_CWD")));
        Console.WriteLine("ENV:" + (Environment.GetEnvironmentVariable("MILLIGRAM_PTY_VALUE") == "value with spaces & Æ"));
        Console.WriteLine($"SIZE:{Console.WindowWidth}x{Console.WindowHeight}");
        Console.WriteLine("READY");
        var input = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.KeyChar == '\u0003')
            {
                input.Clear();
                Console.WriteLine("INTERRUPTED");
                continue;
            }
            if (key.Key != ConsoleKey.Enter)
            {
                if (key.KeyChar != '\0') input.Append(key.KeyChar);
                continue;
            }
            var line = input.ToString();
            input.Clear();
            if (line == "exit") return 17;
            if (line == "size") Console.WriteLine($"SIZE:{Console.WindowWidth}x{Console.WindowHeight}");
            else if (line == "unicode")
            {
                var stream = Console.OpenStandardOutput();
                foreach (var value in Encoding.UTF8.GetBytes("UNICODE:" + Unicode + "\n"))
                {
                    stream.WriteByte(value);
                    stream.Flush();
                    await Task.Delay(2);
                }
            }
            else Console.WriteLine("ACK:" + line);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    /// <summary>Keep a decoder across reads: terminal chunks are bytes, not complete UTF-8 characters.</summary>
    private sealed class Capture
    {
        private readonly Lock gate = new();
        private readonly StringBuilder received = new();
        private readonly Decoder decoder = new UTF8Encoding(false, throwOnInvalidBytes: true).GetDecoder();

        public string Text
        {
            get
            {
                lock (gate) return Regex.Replace(received.ToString(), "\u001B\\[[0-?]*[ -/]*[@-~]", "");
            }
        }

        public async Task Read(Stream stream, CancellationToken cancellation)
        {
            byte[] buffer = new byte[1];
            char[] characters = new char[2];
            try
            {
                while (await stream.ReadAsync(buffer, cancellation) is var count && count > 0)
                {
                    var length = decoder.GetChars(buffer, 0, count, characters, 0, flush: false);
                    lock (gate)
                    {
                        received.Append(characters, 0, length);
                        if (received.Length > 65_536) throw new IOException("Unexpected terminal output exceeded the probe limit");
                    }
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }

        public async Task Wait(string marker)
        {
            var started = Stopwatch.GetTimestamp();
            while (!Text.Contains(marker, StringComparison.Ordinal))
            {
                if (Stopwatch.GetElapsedTime(started) > Deadline) throw new TimeoutException("Missing terminal marker: " + marker);
                await Task.Delay(20);
            }
        }
    }
}
