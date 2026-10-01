using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Milligram.Adapters.Files;
using Milligram.Adapters.Processes;

namespace Milligram.AgentHost.Integration;

/// <summary>Repeated native watcher lifetimes cover idle EOF and source/restore notifications during concurrent changes.</summary>
internal static class WindowsWatcherFixture
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    private static readonly string Prefix = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "Milligram watcher & 漢 ");

    public static async Task Run()
    {
        if (!OperatingSystem.IsWindows()) return;
        foreach (var busy in new[] { false, true })
            await Task.WhenAll(Enumerable.Range(0, 4).Select(index => Scenario(busy, index)));
        Console.WriteLine("PASS: concurrent Windows watchers report source/build inputs and exit after idle or busy input closure");
    }

    private static async Task Scenario(bool busy, int index)
    {
        var root = Prefix + Guid.NewGuid().ToString("N");
        var source = Path.Combine(root, "src");
        var obj = Path.Combine(source, "obj");
        Directory.CreateDirectory(Path.Combine(obj, "Debug", "net10.0"));
        var original = Path.Combine(source, "A.cs");
        var ignored = Path.Combine(obj, "Ignored.cs");
        File.WriteAllText(original, "class A {}\n");
        File.WriteAllText(ignored, "class Ignored {}\n");
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var paths = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var errors = new ConcurrentQueue<string>();
        var script = "[Console]::Error.WriteLine('PowerShell ' + $PSVersionTable.PSVersion + '; CLR ' + [Environment]::Version)\n"
            + WindowsSideWatcher.Script(root) + "\n[Console]::Error.WriteLine('Watcher script completed.')\n";
        using var watcher = ProcessRunner.Follow("powershell.exe",
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))],
            line => { if (line == WindowsSideWatcher.Ready) ready.TrySetResult(); else paths.TryAdd(line, 0); },
            line =>
            {
                line = line.Replace(root, "<fixture>", StringComparison.OrdinalIgnoreCase);
                errors.Enqueue(line.Length > 1024 ? line[..1024] : line);
                while (errors.Count > 8) errors.TryDequeue(out _);
            })
            ?? throw new InvalidOperationException("Could not start the Windows watcher.");
        try
        {
            await ready.Task.WaitAsync(Deadline);
            if (busy)
            {
                File.WriteAllText(ignored, "class Ignored { int x; }\n");
                var assets = Path.Combine(obj, "project.assets.json");
                var usings = Path.Combine(obj, "Debug", "net10.0", "App.GlobalUsings.g.cs");
                File.WriteAllText(assets, "{}");
                File.WriteAllText(usings, "global using System;\n");
                File.WriteAllText(original, "class A { int x; }\n");
                var renamed = Path.Combine(source, "Renamed.cs");
                File.Move(original, renamed);
                File.Delete(renamed);
                for (var file = 0; file < 100; file++) File.WriteAllText(Path.Combine(source, $"Burst{file}.cs"), $"class C{file} {{}}\n");
                using var deadline = new CancellationTokenSource(Deadline);
                while (!new[] { original, renamed, assets, usings, Path.Combine(source, "Burst99.cs") }.All(paths.ContainsKey))
                    await Task.Delay(10, deadline.Token);
                Require(!paths.ContainsKey(ignored), "The watcher reported ignored generated source.");
            }
            else await Task.Delay(250 + index * 100);
            var clock = Stopwatch.StartNew();
            var graceful = watcher.Stop(out var diagnostic);
            Require(graceful && diagnostic.EndsWith("(code 0).", StringComparison.Ordinal),
                $"Watcher busy={busy}, index={index}: {diagnostic}\n{string.Join('\n', errors)}");
            Console.WriteLine($"Watcher busy={busy}, index={index}, close-to-exit={clock.ElapsedMilliseconds} ms; {errors.FirstOrDefault()}");
        }
        finally
        {
            watcher.Dispose();
            var cleanup = Path.GetFullPath(root);
            Require(cleanup.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(cleanup).Contains("..", StringComparison.Ordinal),
                "Unexpected watcher fixture cleanup path.");
            Directory.Delete(cleanup, true);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
