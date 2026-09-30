using System.Reflection;
using Milligram.Adapters.Companion;
using Milligram.Adapters.Processes;
using Milligram.Application;
using Milligram.Domain.Policies;

namespace Milligram.AgentHost.Integration;

/// <summary>A private tmux server exercises session and server replacement without touching user sessions.</summary>
internal static class TmuxFixture
{
    public static async Task Run()
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("tmux requires Unix.");
        var root = "/tmp/mg-tmux-" + Guid.NewGuid().ToString("N")[..8];
        Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var previous = Environment.GetEnvironmentVariable("TMUX_TMPDIR");
        var previousTmux = Environment.GetEnvironmentVariable("TMUX");
        Environment.SetEnvironmentVariable("TMUX_TMPDIR", root);
        Environment.SetEnvironmentVariable("TMUX", null);
        var paths = new ProjectPaths(root);
        var policy = new Policy
        {
            Agent = new AgentSettings
            {
                Command = Program.Dotnet(),
                Args = [Assembly.GetExecutingAssembly().Location, "--tmux-child"],
                Terminal = "none",
            }
        };
        var companion = new TmuxCompanion(paths, () => policy, [Program.Dotnet(), typeof(ProcessRunner).Assembly.Location]);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await KeepServer();
            var original = await companion.StartAsync(deadline.Token) ?? throw new InvalidOperationException("No original tmux ownership.");
            var originalId = Id();
            Require(await companion.StartAsync(deadline.Token) is null, "A reused tmux session acquired ownership.");
            var alias = Path.Combine(root, "alias");
            Directory.CreateSymbolicLink(alias, root);
            var aliasCompanion = new TmuxCompanion(new ProjectPaths(alias), () => policy, [Program.Dotnet(), typeof(ProcessRunner).Assembly.Location]);
            Require(aliasCompanion.SessionName == companion.SessionName, "A project alias changed the tmux session name.");
            Require(aliasCompanion.IsRunning(), "An alias could not find the tmux session.");
            Require(await aliasCompanion.StartAsync(deadline.Token) is null, "An alias created a second tmux session.");
            aliasCompanion.Ring();
            original.Stop();
            Require(!companion.IsRunning(), "Owned cleanup did not stop its tmux session.");
            var replacement = await companion.StartAsync(deadline.Token) ?? throw new InvalidOperationException("No replacement tmux ownership.");
            Require(Id() != originalId, "A live tmux server reused a session id.");
            original.Stop();
            Require(companion.IsRunning(), "Old cleanup stopped a replacement tmux session.");
            aliasCompanion.Stop();
            Require(!companion.IsRunning(), "Explicit stop did not stop the current tmux session.");

            Tmux("kill-server");
            await KeepServer();
            var restarted = await companion.StartAsync(deadline.Token) ?? throw new InvalidOperationException("No restarted tmux ownership.");
            Require(Id() == originalId, "The fixture did not reproduce session id reuse after a server restart.");
            original.Stop();
            replacement.Stop();
            Require(companion.IsRunning(), "Old cleanup stopped a new server's session with a reused id.");
            restarted.Stop();
            Require(!companion.IsRunning(), "The new owner could not stop its tmux session.");
            Console.WriteLine("PASS: tmux generation cleanup, name reuse, server restart and numeric id reuse");
            Console.WriteLine("PASS: project aliases reuse and control the same tmux session");

            string Id()
            {
                var result = Tmux("display-message", "-p", "-t", companion.SessionName, "#{session_id}");
                Require(result.ExitCode == 0, "Could not read tmux session id: " + result.Output);
                return result.Output.Trim();
            }
            async Task KeepServer()
            {
                while (Tmux("-f", "/dev/null", "new-session", "-d", "-s", "keeper", "sleep 60").ExitCode != 0)
                    await Task.Delay(50, deadline.Token);
            }
        }
        finally
        {
            Tmux("kill-server");
            Environment.SetEnvironmentVariable("TMUX_TMPDIR", previous);
            Environment.SetEnvironmentVariable("TMUX", previousTmux);
            Directory.Delete(root, recursive: true);
        }
    }

    private static (int ExitCode, string Output) Tmux(params string[] args) => ProcessRunner.Capture("tmux", args);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
