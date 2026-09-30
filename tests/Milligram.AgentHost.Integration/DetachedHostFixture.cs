using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Milligram.Adapters.Companion;
using Milligram.Adapters.Processes;
using Milligram.Application;
using Milligram.Domain.Policies;

namespace Milligram.AgentHost.Integration;

/// <summary>The actual hidden command outlives its launcher, converges on one lease and accepts replacement clients.</summary>
internal static class DetachedHostFixture
{
    public static async Task Run()
    {
        var fixture = Path.Combine(Path.GetTempPath(), "Milligram detached & 漢 " + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(fixture);
        var marker = Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(Path.Combine(fixture, "cwd-marker"), marker);
        var paths = new ProjectPaths(fixture);
        var files = new AgentHostFiles(paths);
        JsonFile.Write(paths.PolicyFile, new Policy
        {
            Agent = new AgentSettings
            {
                Command = Program.Dotnet(),
                Args = [Assembly.GetExecutingAssembly().Location, "--child", Program.Argument, marker],
            },
        });
        Process? hostProcess = null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var token = deadline.Token;
        Exception? failure = null;
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (attempt == 1) JsonFile.Write(files.DiscoveryFile, new AgentHostDiscovery(123, "stale", 99, "old", DateTimeOffset.UnixEpoch));
                var launchedPid = Launch();
                hostProcess = Process.GetProcessById(launchedPid);
                _ = hostProcess.SafeHandle;
                await Until(() => AgentHostLease.ReadDiscovery(files) is { Version: not "old" });
                var discovery = AgentHostLease.ReadDiscovery(files)!;
                Require(discovery.Pid == launchedPid, "Discovery did not identify the host that was launched.");
                Require(!hostProcess.HasExited, "The host did not outlive its launching process.");
                if (!OperatingSystem.IsWindows()) Require(SessionId(discovery.Pid) == discovery.Pid, "The host did not create its own Unix session.");
                var conversation = await File.ReadAllTextAsync(paths.AgentStateFile, token);
                await ReadClient(stop: false);

                if (attempt == 0)
                {
                    var duplicatePid = Launch();
                    Process? duplicate = null;
                    try { duplicate = Process.GetProcessById(duplicatePid); }
                    catch (ArgumentException) { }
                    if (duplicate is not null)
                    {
                        using (duplicate) await duplicate.WaitForExitAsync(token);
                    }
                    Require(AgentHostLease.ReadDiscovery(files) == discovery, "A duplicate start replaced the owner's discovery.");
                    Require(await File.ReadAllTextAsync(paths.AgentStateFile, token) == conversation, "A duplicate start changed the conversation.");
                }

                await ReadClient(stop: true);
                await hostProcess.WaitForExitAsync(token);
                Require(AgentHostLease.ReadDiscovery(files) is null, "The host retained discovery after exit.");
                hostProcess.Dispose();
                hostProcess = null;
            }
            var policy = JsonFile.Read<Policy>(paths.PolicyFile)!;
            var companion = new AgentHostCompanion(paths, () => policy, [Program.Dotnet(), typeof(ProcessRunner).Assembly.Location],
                "integration", ProcessRunner.OnPath, ProcessRunner.StartDetached);
            await companion.StartAsync(token);
            hostProcess = Process.GetProcessById(AgentHostLease.ReadDiscovery(files)!.Pid);
            _ = hostProcess.SafeHandle;
            await companion.StartAsync(token);
            Require(companion.IsRunning(), "The controller did not find the host it started.");
            await ReadClient(stop: true, companion);
            await hostProcess.WaitForExitAsync(token);
            Require(!companion.IsRunning(), "The controller still sees a stopped host.");
            Require(AgentHostLease.ReadDiscovery(files) is null, "Controller stop returned before discovery was removed.");
            hostProcess.Dispose();
            hostProcess = null;
            Console.WriteLine("PASS: detached command, duplicate start, reconnect, stale discovery and restart");
            Console.WriteLine("PASS: native companion start, reuse, status, notification and stop");

            int Launch()
            {
                var watch = Stopwatch.StartNew();
                var result = ProcessRunner.Capture(Program.Dotnet(), Assembly.GetExecutingAssembly().Location, "--launch-host", fixture);
                Require(result.ExitCode == 0, "The detached launcher failed: " + result.Output);
                Require(watch.Elapsed < TimeSpan.FromSeconds(10), "The detached host retained its launcher's standard streams.");
                return int.Parse(result.Output.Trim());
            }

            async Task Until(Func<bool> predicate)
            {
                while (!predicate()) await Task.Delay(20, token);
            }

            async Task ReadClient(bool stop, AgentHostCompanion? controller = null)
            {
                using var connectionLifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
                await using var client = await AgentPipeClient.ConnectAsync(files.Endpoint, new HostHello(HostProtocol.Version, "integration"), token);
                var capture = new Program.Capture();
                var reading = capture.Read(client, connectionLifetime.Token);
                try
                {
                    await capture.Wait("READY", token);
                    if (stop)
                    {
                        if (controller is null) await client.SendAsync(new HostFrame(HostFrameKind.Ring, []), token);
                        else await Task.Run(controller.Ring, token);
                        await capture.Wait("ACK:" + AgentBriefing.Doorbell, token);
                        if (controller is null) await client.SendAsync(new HostFrame(HostFrameKind.Stop, []), token);
                        else await Task.Run(controller.Stop, token);
                        await reading.WaitAsync(token);
                        Require(capture.Exited.Task.IsCompletedSuccessfully, "The detached host omitted its exit frame.");
                    }
                    else
                    {
                        await client.SendAsync(new HostFrame(HostFrameKind.Input, Encoding.UTF8.GetBytes("unicode\r")), token);
                        await capture.Wait("UNICODE:Grüße ☃ 漢字 🐱", token);
                    }
                }
                finally
                {
                    connectionLifetime.Cancel();
                    try { await reading; }
                    catch (OperationCanceledException) when (connectionLifetime.IsCancellationRequested) { }
                }
            }
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            if (hostProcess is not null)
            {
                if (!hostProcess.HasExited) hostProcess.Kill(entireProcessTree: true);
                await hostProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                hostProcess.Dispose();
            }
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(fixture).StartsWith(temp, StringComparison.Ordinal))
            {
                try { Directory.Delete(fixture, recursive: true); }
                catch (IOException) when (failure is not null) { Console.Error.WriteLine("Failed fixture retained at " + fixture); }
            }
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    [DllImport("libc", EntryPoint = "getsid", SetLastError = true)]
    private static extern int SessionId(int pid);
}
