using System.Reflection;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Milligram.Adapters.Companion;
using Milligram.Adapters.Processes;
using Milligram.Application;
using Milligram.Domain.Policies;

namespace Milligram.AgentHost.Integration;

/// <summary>Runs the public attach command inside a real terminal while another pipe client stays connected.</summary>
internal static class AttachmentFixture
{
    public static async Task<int> Child(string project)
    {
        _ = Console.WindowWidth;
        var before = Modes();
        var code = await Milligram.Main.Program.Main(["agent", "attach", "--project", project]);
        var after = Modes();
        Require(before.SequenceEqual(after), "Attachment did not restore the console modes: " +
            string.Join(", ", Enumerable.Range(0, before.Length).Where(i => before[i] != after[i]).Select(i => $"{i}:{before[i]:x2}->{after[i]:x2}")));
        Console.WriteLine("RESTORED:" + code);
        Console.WriteLine("SHELL_READY");
        Console.WriteLine("AFTER:" + Console.ReadLine());
        return 0;
    }

    public static async Task Run()
    {
        var fixture = Path.Combine(Path.GetTempPath(), "Milligram attach & 漢 " + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(fixture);
        var paths = new ProjectPaths(fixture);
        var marker = Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(Path.Combine(fixture, "cwd-marker"), marker);
        var policy = new Policy
        {
            Agent = new AgentSettings
            {
                Host = AgentHostKind.Milligram,
                Command = Program.Dotnet(),
                Args = [Assembly.GetExecutingAssembly().Location, "--child", Program.Argument, marker],
            },
        };
        JsonFile.Write(paths.PolicyFile, policy);
        var companion = new AgentHostCompanion(paths, () => policy, [Program.Dotnet(), typeof(ProcessRunner).Assembly.Location],
            "integration", ProcessRunner.OnPath, ProcessRunner.StartDetached);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            Require(await companion.StartAsync(lifetime.Token) is not null, "Attachment fixture did not start its own host.");
            var files = new AgentHostFiles(paths);
            var original = AgentHostLease.ReadDiscovery(files);
            await using var observer = await AgentPipeClient.ConnectAsync(files.Endpoint, new HostHello(1, "integration"), lifetime.Token);
            var rounds = OperatingSystem.IsWindows() ? 3 : 4;
            for (var round = 1; round <= rounds; round++)
            {
                var launch = new AgentLaunch(Program.Dotnet(), [Assembly.GetExecutingAssembly().Location, "--attach-console", fixture],
                    fixture, paths.RunDirectory);
                using var terminal = await ProcessRunner.StartTerminalAsync(launch, new TerminalSize(80, 24), lifetime.Token);
                using var readingLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                var capture = new Program.Capture();
                var reading = capture.Read(terminal.Output, readingLifetime.Token);
                try
                {
                    await capture.Wait("READY", lifetime.Token);
                    await UntilStatus(s => s.Clients == 2);
                    await Send($"round{round} Grüße 漢 🐱\r");
                    await capture.Wait($"ACK:round{round} Grüße 漢 🐱", lifetime.Token);
                    if (round == 1)
                    {
                        await Send("unicode\r");
                        await capture.Wait("UNICODE:Grüße ☃ 漢字 🐱", lifetime.Token);
                        await Send("\u0003");
                        await capture.Wait("INTERRUPTED", lifetime.Token);
                        terminal.Resize(new TerminalSize(96, 31));
                        await UntilStatus(s => s.Columns == 96 && s.Rows == 31);
                        await Send("size\r");
                        await capture.Wait("SIZE:96x31", lifetime.Token);
                        await Send("\u001d\u001d\r");
                        await capture.Wait("ACK:\u001d", lifetime.Token);
                    }
                    if (round == rounds) await Send("exit\r");
                    else if (round == 3) Require(Signal(terminal.Pid, 15) == 0, "Cannot signal the attachment.");
                    else { await Send("\u001d"); await Task.Delay(20, lifetime.Token); await Send("d"); }
                    await capture.Wait("RESTORED:" + (round == rounds ? 17 : 0), lifetime.Token);
                    await capture.Wait("SHELL_READY", lifetime.Token);
                    await Send($"shell{round}\r");
                    await capture.Wait("AFTER:shell" + round, lifetime.Token);
                    Require(await terminal.Exited.WaitAsync(lifetime.Token) == 0, "The attachment wrapper failed.");
                    await reading.WaitAsync(lifetime.Token);
                    if (round < rounds)
                    {
                        Require(AgentHostLease.ReadDiscovery(files) == original, "Detaching changed the host's identity.");
                        await UntilStatus(s => s.Clients == 1);
                    }
                }
                catch (Exception error) { throw new InvalidOperationException("Attach round " + round + "\n" + capture.Text, error); }
                finally
                {
                    terminal.Stop();
                    readingLifetime.Cancel();
                    try { await reading; }
                    catch (OperationCanceledException) when (readingLifetime.IsCancellationRequested) { }
                }

                async Task Send(string text)
                {
                    await terminal.Input.WriteAsync(Encoding.UTF8.GetBytes(text), lifetime.Token);
                    await terminal.Input.FlushAsync(lifetime.Token);
                }
            }
            Console.WriteLine("PASS: native CLI attach, Unicode, Ctrl+C, resize, detach, reattach, concurrent client and mode restoration");
            if (!OperatingSystem.IsWindows()) Console.WriteLine("PASS: terminal modes restored after SIGTERM");

            async Task UntilStatus(Func<AgentHostStatus, bool> ready)
            {
                while (true)
                {
                    await observer.SendAsync(new HostFrame(HostFrameKind.Status, []), lifetime.Token);
                    while (await observer.ReadAsync(lifetime.Token) is { } frame)
                    {
                        if (frame.Kind != HostFrameKind.Status) continue;
                        if (ready(HostProtocol.ReadJson<AgentHostStatus>(frame))) return;
                        break;
                    }
                    await Task.Delay(20, lifetime.Token);
                }
            }
        }
        finally
        {
            companion.Stop();
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(fixture).StartsWith(temp, StringComparison.Ordinal)) Directory.Delete(fixture, recursive: true);
        }
    }

    private static byte[] Modes()
    {
        if (OperatingSystem.IsWindows())
        {
            Require(GetConsoleMode(GetStdHandle(-10), out var input) && GetConsoleMode(GetStdHandle(-11), out _), "Cannot read console modes.");
            GetConsoleMode(GetStdHandle(-11), out var output);
            return [.. BitConverter.GetBytes(input), .. BitConverter.GetBytes(output)];
        }
        var modes = new byte[256];
        Require(GetAttributes(0, modes) == 0, "Cannot read terminal modes.");
        if (OperatingSystem.IsMacOS())
        {
            // Darwin sets PENDIN when restoring ICANON; this is pending-input state, not a saved mode.
            var flags = BinaryPrimitives.ReadUInt64LittleEndian(modes.AsSpan(24));
            BinaryPrimitives.WriteUInt64LittleEndian(modes.AsSpan(24), flags & ~0x20000000ul);
        }
        return modes;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    [DllImport("kernel32.dll")] private static extern nint GetStdHandle(int standard);
    [DllImport("kernel32.dll")] private static extern bool GetConsoleMode(nint handle, out uint mode);
    [DllImport("libc", EntryPoint = "tcgetattr")] private static extern int GetAttributes(int descriptor, [Out] byte[] modes);
    [DllImport("libc", EntryPoint = "kill")] private static extern int Signal(int pid, int signal);
}
