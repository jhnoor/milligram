using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Milligram.Adapters.Companion;
using Milligram.Adapters.Processes;
using Milligram.Application;
using Milligram.Domain.Policies;

namespace Milligram.AgentHost.Integration;

/// <summary>Real terminals and owned fixture processes; deliberately outside the fast unit-test solution.</summary>
internal static class Program
{
    internal const string Argument = "a \"quoted\" argument & 漢";
    private const string Unicode = "Grüße ☃ 漢字 🐱";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args.FirstOrDefault() == "--child") return await Child(args);
        if (args.FirstOrDefault() == "--record-terminal")
        {
            JsonFile.Write(Path.Combine(args[^1], ".milligram", "run", "terminal-invocation.json"), args[1..]);
            return 0;
        }
        if (args.FirstOrDefault() == "--grandchild") return await Grandchild(args[1]);
        if (args.FirstOrDefault() == "--attach-console") return await AttachmentFixture.Child(args[1]);
        if (args.FirstOrDefault() == "--attach-fixture") { await AttachmentFixture.Run(); return 0; }
        if (args.FirstOrDefault() == "--tmux-fixture") { await TmuxFixture.Run(); return 0; }
        if (args.FirstOrDefault() == "--tmux-child") { while (Console.ReadLine() is not null) { } return 0; }
        if (args.FirstOrDefault() == "--launch-host")
        {
            using var host = ProcessRunner.StartDetached(Dotnet(), [typeof(ProcessRunner).Assembly.Location, "agent", "host", "--project", args[1]], args[1]);
            if (host is null) return 1;
            Console.WriteLine(host.Id);
            return 0;
        }
        var rid = Array.IndexOf(args, "--expected-rid");
        if (rid >= 0 && RuntimeInformation.RuntimeIdentifier != args[rid + 1]) throw new InvalidOperationException("Unexpected runner architecture.");
        Console.WriteLine($"Native agent adapter: {RuntimeInformation.RuntimeIdentifier}; {RuntimeInformation.OSDescription}");
        try
        {
            if (ProcessRunner.TerminalIssue() is { } issue) throw new InvalidOperationException(issue);
            foreach (var hosted in new[] { false, true })
            {
                await Scenario(tree: false, parentExits: false, hosted);
                await Scenario(tree: true, parentExits: false, hosted);
                await Scenario(tree: true, parentExits: true, hosted);
            }
            await DetachedHostFixture.Run();
            await AttachmentFixture.Run();
            if (!OperatingSystem.IsWindows()) await TmuxFixture.Run();
            Console.WriteLine("PASS: terminal controls and tree cleanup directly and through the host runtime and pipe");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static async Task Scenario(bool tree, bool parentExits, bool hosted)
    {
        var fixture = Path.Combine(Path.GetTempPath(), "Milligram agent & 漢 " + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(fixture);
        var marker = Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(Path.Combine(fixture, "cwd-marker"), marker);
        var paths = new ProjectPaths(fixture);
        var command = Dotnet();
        var policy = new Policy
        {
            Agent = new AgentSettings { Command = command, Args = [Assembly.GetExecutingAssembly().Location, "--child", Argument, marker] },
        };
        AgentBriefing.Write(paths);
        var launch = new AgentLaunches(paths, () => policy, [command, typeof(ProcessRunner).Assembly.Location]).Prepare(OperatingSystem.IsWindows());
        IAgentTerminal? terminal = null;
        AgentSession? session = null;
        AgentPipeServer? server = null;
        AgentPipeClient? client = null;
        Task<int>? running = null;
        Process? descendant = null;
        Task? reading = null;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var capture = new Capture();
        try
        {
            terminal = await ProcessRunner.StartTerminalAsync(launch, new TerminalSize(80, 24), cancellation.Token).WaitAsync(Deadline);
            if (hosted)
            {
                var endpoint = "mg-fixture-" + Guid.NewGuid().ToString("N")[..16];
                var greeting = new HostHello(HostProtocol.Version, "integration");
                session = new AgentSession(terminal, new TerminalSize(80, 24));
                server = new AgentPipeServer(session, endpoint, greeting, Console.Error.WriteLine);
                var runtime = new AgentHostRuntime(terminal, session, server, Console.Error.WriteLine);
                running = runtime.RunAsync(() => { }, cancellation.Token);
                await server.Ready.WaitAsync(Deadline);
                client = await AgentPipeClient.ConnectAsync(endpoint, greeting, cancellation.Token);
            }
            reading = client is null ? capture.Read(terminal.Output, cancellation.Token) : capture.Read(client, cancellation.Token);
            await capture.Wait("READY", cancellation.Token);
            foreach (var expected in new[] { "TTY:True", "ARG:True", "CWD:True", "SHIM:True", "SIZE:80x24" })
                Require(capture.Text.Contains(expected, StringComparison.Ordinal), "Missing " + expected + "\n" + capture.Text);
            if (tree)
            {
                await Send("spawn\r");
                await capture.Wait("GRANDCHILD_READY", cancellation.Token);
                var pid = int.Parse(await File.ReadAllTextAsync(Path.Combine(fixture, "grandchild.pid"), cancellation.Token));
                descendant = Process.GetProcessById(pid);
                Require(!descendant.HasExited, "The tree fixture exited before cleanup was tested.");
                if (parentExits)
                {
                    await Send("exit\r");
                    Require(await ExitCode().WaitAsync(Deadline) == 17, "Lost the parent's nonzero exit code.");
                }
                if (!parentExits || client is null) await Stop();
                await ExitCode().WaitAsync(Deadline);
            }
            else
            {
                await Send("unicode\r");
                await capture.Wait("UNICODE:" + Unicode, cancellation.Token);
                var size = new TerminalSize(96, 31);
                if (client is null) terminal.Resize(size);
                else await client.SendAsync(HostProtocol.Resize(size), cancellation.Token);
                if (!OperatingSystem.IsWindows()) await capture.Wait("SIGWINCH", cancellation.Token);
                var started = Stopwatch.GetTimestamp();
                while (!capture.Text.Contains("SIZE:96x31", StringComparison.Ordinal))
                {
                    Require(Stopwatch.GetElapsedTime(started) < Deadline, "The child did not see its new dimensions.");
                    await Send("size\r");
                    await Task.Delay(20, cancellation.Token);
                }
                await Send("\u0003");
                await capture.Wait("INTERRUPTED", cancellation.Token);
                await Send(AgentBriefing.Doorbell);
                await Task.Delay(150, cancellation.Token);
                Require(!capture.Text.Contains("ACK:" + AgentBriefing.Doorbell, StringComparison.Ordinal), "The doorbell submitted before its return.");
                await Send("\r");
                await capture.Wait("ACK:" + AgentBriefing.Doorbell, cancellation.Token);
                await Send("exit\r");
                Require(await ExitCode().WaitAsync(Deadline) == 17, "Lost exit code 17.");
                if (client is null) terminal.Stop();
            }
            await reading.WaitAsync(Deadline);
            if (running is null) await Task.Run(terminal.Dispose).WaitAsync(Deadline);
            else Require(await running.WaitAsync(Deadline) == 0, "The host reported a lifecycle failure.");
            terminal = null;
            if (descendant is not null)
            {
                await descendant.WaitForExitAsync(cancellation.Token).WaitAsync(Deadline);
                Require(descendant.HasExited, "An owned descendant survived terminal cleanup.");
            }
            Console.WriteLine($"PASS ({(hosted ? "hosted" : "direct")}): {(tree ? parentExits ? "parent exits before descendant" : "stop with descendant" : "terminal controls and nonzero exit")}");

            async Task Send(string text)
            {
                if (client is not null) await client.SendAsync(new HostFrame(HostFrameKind.Input, Encoding.UTF8.GetBytes(text)), cancellation.Token);
                else
                {
                    await terminal.Input.WriteAsync(Encoding.UTF8.GetBytes(text), cancellation.Token);
                    await terminal.Input.FlushAsync(cancellation.Token);
                }
            }

            Task<int> ExitCode() => client is null ? terminal.Exited : capture.Exited.Task;

            async Task Stop()
            {
                if (client is null) terminal.Stop();
                else await client.SendAsync(new HostFrame(HostFrameKind.Stop, []), cancellation.Token);
            }
        }
        finally
        {
            cancellation.Cancel();
            try
            {
                if (running is not null) await running.WaitAsync(Deadline);
                else if (terminal is not null) await Task.Run(terminal.Dispose).WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                if (client is not null) await client.DisposeAsync();
                server?.Dispose();
                session?.Dispose();
                if (descendant is not null)
                {
                    if (!descendant.HasExited) descendant.Kill(entireProcessTree: true);
                    descendant.Dispose();
                }
                if (reading is not null)
                {
                    try { await reading.WaitAsync(TimeSpan.FromSeconds(5)); }
                    catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
                }
                var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (Path.GetFullPath(fixture).StartsWith(temp, StringComparison.Ordinal)) Directory.Delete(fixture, recursive: true);
            }
        }
    }

    private static async Task<int> Child(string[] args)
    {
        Console.InputEncoding = new UTF8Encoding(false);
        using var resized = OperatingSystem.IsWindows() ? null
            : PosixSignalRegistration.Create(PosixSignal.SIGWINCH, context => { context.Cancel = true; Console.WriteLine("SIGWINCH"); });
        var attached = !Console.IsInputRedirected && !Console.IsOutputRedirected;
        if (!OperatingSystem.IsWindows())
        {
            using var controlling = File.Open("/dev/tty", FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            attached &= controlling.CanRead;
        }
        Console.WriteLine("TTY:" + attached);
        Console.WriteLine("ARG:" + (args[1] == Argument));
        Console.WriteLine("CWD:" + (File.ReadAllText("cwd-marker") == args[2]));
        var shim = ProcessRunner.Capture("milligram", "--version");
        Console.WriteLine("SHIM:" + (shim.ExitCode == 0 && shim.Output.Contains("0.0.0", StringComparison.Ordinal)));
        Console.TreatControlCAsInput = true;
        Console.WriteLine($"SIZE:{Console.WindowWidth}x{Console.WindowHeight}");
        Console.WriteLine("READY");
        var input = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            // Unix Console.ReadKey maps the Ctrl+] byte to Ctrl+5 with an empty KeyChar.
            var character = key.KeyChar == '\0' && key.Key == ConsoleKey.D5 && key.Modifiers.HasFlag(ConsoleModifiers.Control)
                ? '\u001d' : key.KeyChar;
            if (character == '\u0003') { input.Clear(); Console.WriteLine("INTERRUPTED"); continue; }
            if (key.Key != ConsoleKey.Enter) { if (character != '\0') input.Append(character); continue; }
            var line = input.ToString();
            input.Clear();
            if (line == "exit") return 17;
            if (line == "spawn")
            {
                var pidFile = Path.Combine(Environment.CurrentDirectory, "grandchild.pid");
                Require(ProcessRunner.Launch(Dotnet(), [Assembly.GetExecutingAssembly().Location, "--grandchild", pidFile]), "Could not start owned descendant.");
                var started = Stopwatch.GetTimestamp();
                while (!File.Exists(pidFile))
                {
                    Require(Stopwatch.GetElapsedTime(started) < Deadline, "Descendant did not start.");
                    await Task.Delay(20);
                }
                Console.WriteLine("GRANDCHILD_READY");
            }
            else if (line == "size") Console.WriteLine($"SIZE:{Console.WindowWidth}x{Console.WindowHeight}");
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

    private static async Task<int> Grandchild(string pidFile)
    {
        using var hangup = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGHUP, context => context.Cancel = true);
        using var terminate = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => context.Cancel = true);
        JsonFile.Write(pidFile, Environment.ProcessId);
        await Task.Delay(Timeout.Infinite);
        return 0;
    }

    internal static string Dotnet()
    {
        var runtime = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
        return Path.Combine(runtime.Parent!.Parent!.Parent!.FullName, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal sealed class Capture
    {
        private readonly Lock gate = new();
        private readonly StringBuilder received = new();
        private readonly Decoder decoder = new UTF8Encoding(false, throwOnInvalidBytes: true).GetDecoder();
        public TaskCompletionSource<int> Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Text { get { lock (gate) return Regex.Replace(received.ToString(), "\u001B\\[[0-?]*[ -/]*[@-~]", ""); } }

        public async Task Read(AgentPipeClient client, CancellationToken cancellation)
        {
            while (await client.ReadAsync(cancellation) is { } frame)
            {
                Require(!Exited.Task.IsCompleted, "The host sent output after exit.");
                if (frame.Kind == HostFrameKind.Exited) Exited.TrySetResult(HostProtocol.ReadExitCode(frame));
                else
                {
                    Require(frame.Kind == HostFrameKind.Output, "Unexpected host frame.");
                    using var chunk = new MemoryStream(frame.Payload, writable: false);
                    await Read(chunk, cancellation);
                }
            }
            Require(Exited.Task.IsCompleted, "The host closed without an exit frame.");
        }

        public async Task Read(Stream stream, CancellationToken cancellation)
        {
            var buffer = new byte[1];
            var characters = new char[2];
            while (await stream.ReadAsync(buffer, cancellation) is var count && count > 0)
            {
                var length = decoder.GetChars(buffer, 0, count, characters, 0, flush: false);
                lock (gate)
                {
                    received.Append(characters, 0, length);
                    Require(received.Length <= 65_536, "Fixture output exceeded its limit.");
                }
            }
        }

        public async Task Wait(string marker, CancellationToken cancellation)
        {
            var started = Stopwatch.GetTimestamp();
            while (!Text.Contains(marker, StringComparison.Ordinal))
            {
                Require(Stopwatch.GetElapsedTime(started) < Deadline, "Missing terminal marker: " + marker + "\n" + Text);
                await Task.Delay(20, cancellation);
            }
        }
    }
}
