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
        var fixture = Path.Combine(Path.GetTempPath(), "Milligram detached & 漢; " + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(fixture);
        var marker = Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(Path.Combine(fixture, "cwd-marker"), marker);
        var paths = new ProjectPaths(fixture);
        var files = new AgentHostFiles(paths);
        var alias = fixture + ".alias";
        JsonFile.Write(paths.PolicyFile, new Policy
        {
            Agent = new AgentSettings
            {
                Host = AgentHostKind.Milligram,
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
            if (OperatingSystem.IsWindows())
            {
                var link = ProcessRunner.Capture("pwsh", "-NoProfile", "-NonInteractive", "-Command",
                    $"$ErrorActionPreference = 'Stop'; New-Item -ItemType Junction -Path '{alias.Replace("'", "''", StringComparison.Ordinal)}' -Target '{fixture.Replace("'", "''", StringComparison.Ordinal)}' | Out-Null");
                Require(link.ExitCode == 0, "Could not create the project junction: " + link.Output);
            }
            else Directory.CreateSymbolicLink(alias, fixture);
            var aliasPaths = new ProjectPaths(alias);
            var aliasFiles = new AgentHostFiles(aliasPaths);
            Require(files.Endpoint == aliasFiles.Endpoint, "An alias changed the host endpoint.");
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
            var aliasCompanion = new AgentHostCompanion(aliasPaths, () => policy, [Program.Dotnet(), typeof(ProcessRunner).Assembly.Location],
                "integration", ProcessRunner.OnPath, ProcessRunner.StartDetached);
            var starts = await Task.WhenAll(companion.StartAsync(token), companion.StartAsync(token));
            Require(starts.Count(started => started is not null) == 1, "Concurrent starters did not report exactly one owner.");
            var ownership = starts.Single(started => started is not null)!;
            hostProcess = Process.GetProcessById(AgentHostLease.ReadDiscovery(files)!.Pid);
            _ = hostProcess.SafeHandle;
            Require(await companion.StartAsync(token) is null, "A reused host incorrectly granted startup ownership.");
            Require(await aliasCompanion.StartAsync(token) is null, "An alias started another host or acquired ownership.");
            await Until(aliasCompanion.IsRunning);
            for (var probe = 0; probe < 10; probe++)
                await Until(companion.IsRunning);
            await ReadClient(stop: true, aliasCompanion, aliasFiles.Endpoint);
            await hostProcess.WaitForExitAsync(token);
            Require(!companion.IsRunning(), "The controller still sees a stopped host.");
            Require(AgentHostLease.ReadDiscovery(files) is null, "Controller stop returned before discovery was removed.");
            hostProcess.Dispose();
            hostProcess = null;
            var replacement = await companion.StartAsync(token) ?? throw new InvalidOperationException("The replacement has no owner.");
            hostProcess = Process.GetProcessById(AgentHostLease.ReadDiscovery(files)!.Pid);
            _ = hostProcess.SafeHandle;
            await Task.Run(ownership.Stop, token);
            await using (var client = await AgentPipeClient.ConnectAsync(files.Endpoint, new HostHello(HostProtocol.Version, "integration"), token))
                Require(client.Greeting?.Instance == AgentHostLease.ReadDiscovery(files)?.Instance, "An old owner stopped or replaced the new host.");
            await Task.Run(replacement.Stop, token);
            await hostProcess.WaitForExitAsync(token);
            hostProcess.Dispose();
            hostProcess = null;
            Console.WriteLine("PASS: detached command, duplicate start, reconnect, stale discovery and restart");
            Console.WriteLine("PASS: native companion concurrent start, generation ownership, reuse, status, notification and stop");
            Console.WriteLine("PASS: project aliases reuse the native host and can attach, ring and stop it");

            policy = policy with
            {
                Agent = policy.Agent with
                {
                    Terminal = $"\"{Program.Dotnet()}\" \"{Assembly.GetExecutingAssembly().Location}\" --record-terminal {{command}}",
                },
            };
            JsonFile.Write(paths.PolicyFile, policy);
            var started = AgentCommand("start");
            Require(started.ExitCode == 0, "The opt-in agent start command failed: " + started.Output);
            hostProcess = Process.GetProcessById(AgentHostLease.ReadDiscovery(files)!.Pid);
            _ = hostProcess.SafeHandle;
            var invocation = Path.Combine(paths.RunDirectory, "terminal-invocation.json");
            await Until(() => File.Exists(invocation));
            Require(JsonFile.Read<string[]>(invocation)!.SequenceEqual(
                [Program.Dotnet(), typeof(ProcessRunner).Assembly.Location, "agent", "attach", "--project", fixture]),
                "The custom terminal did not receive this build and project as separate arguments.");
            var previousTrace = Environment.GetEnvironmentVariable("MILLIGRAM_TRACE_AGENT_PROBES");
            try
            {
                Environment.SetEnvironmentVariable("MILLIGRAM_TRACE_AGENT_PROBES", "1");
                var status = AgentCommand("status");
                Require(status.ExitCode == 0 && status.Output.Contains("running: milligram agent attach", StringComparison.Ordinal),
                    "The opt-in agent status command lost its host: " + status.Output);
                Require(status.Output.Contains("Agent probe (elapsed ms):", StringComparison.Ordinal), "The requested probe diagnostics were missing.");
                for (var round = 1; round <= 3; round++)
                {
                    Console.WriteLine($"Fresh status processes: round {round}/3");
                    var probes = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => AgentCommand("status"), token)));
                    foreach (var probe in probes) Console.WriteLine(probe.Output.Trim());
                    Require(probes.All(probe => probe.ExitCode == 0 && probe.Output.Contains("running: milligram agent attach", StringComparison.Ordinal)),
                        "A fresh status process lost the running host under concurrent startup load.");
                    Require(probes.All(probe => probe.Output.Contains("connection-window=", StringComparison.Ordinal)), "A fresh status process omitted its connection-window timing.");
                }
            }
            finally { Environment.SetEnvironmentVariable("MILLIGRAM_TRACE_AGENT_PROBES", previousTrace); }
            var stopped = AgentCommand("stop");
            Require(stopped.ExitCode == 0, "The opt-in agent stop command failed: " + stopped.Output);
            await hostProcess.WaitForExitAsync(token);
            Require(AgentHostLease.ReadDiscovery(files) is null, "The agent stop command returned before discovery was removed.");
            hostProcess.Dispose();
            hostProcess = null;
            Console.WriteLine("PASS: opt-in agent start, status, stop and custom terminal argument boundaries");

            var unknown = AgentCommand("unknown");
            Require(unknown.ExitCode == 64, "An unknown agent command did not return usage: " + unknown.Output);
            await File.WriteAllTextAsync(paths.PolicyFile, """{"agent":{"host":"unknown"}}""", token);
            var invalid = AgentCommand("start");
            Require(invalid.ExitCode == 1 && invalid.Output.Contains("milligram.json", StringComparison.Ordinal),
                "An invalid host setting did not return a configuration error: " + invalid.Output);
            Require(AgentHostLease.ReadDiscovery(files) is null, "An invalid host setting started an agent.");
            Console.WriteLine("PASS: invalid agent commands and configuration cannot start a host");

            (int ExitCode, string Output) AgentCommand(string command) => ProcessRunner.Capture(Program.Dotnet(),
                typeof(ProcessRunner).Assembly.Location, "agent", command, "--project", command == "start" ? fixture : alias);

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

            async Task ReadClient(bool stop, AgentHostCompanion? controller = null, string? endpoint = null)
            {
                using var connectionLifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
                await using var client = await AgentPipeClient.ConnectAsync(endpoint ?? files.Endpoint, new HostHello(HostProtocol.Version, "integration"), token);
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
        catch (Exception error)
        {
            failure = error;
            Console.Error.WriteLine($"Detached fixture failed: host exited = {hostProcess?.HasExited}, discovery = {AgentHostLease.ReadDiscovery(files)}");
            try
            {
                using var log = new StreamReader(new FileStream(files.LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
                Console.Error.WriteLine(await log.ReadToEndAsync());
                using var probeDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var watch = Stopwatch.StartNew();
                await using var probe = await AgentPipeClient.ConnectAsync(files.Endpoint, new HostHello(HostProtocol.Version, "diagnostic"), probeDeadline.Token);
                Console.Error.WriteLine($"Diagnostic async hello succeeded in {watch.ElapsedMilliseconds} ms.");
            }
            catch (Exception diagnostic) { Console.Error.WriteLine("Diagnostic probe failed: " + diagnostic.Message); }
            throw;
        }
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
                if (Directory.Exists(alias)) Directory.Delete(alias);
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
