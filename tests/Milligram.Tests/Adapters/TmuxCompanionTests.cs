using Milligram.Adapters.Companion;
using Milligram.Application;
using Milligram.Domain.Policies;

namespace Milligram.Tests.Adapters;

public class TmuxCompanionTests
{
    [Fact]
    public async Task ControlsRequireTheExactSessionName()
    {
        using var fixture = new Fixture();
        await fixture.Companion.StartAsync(CancellationToken.None);
        var target = "=" + fixture.Companion.SessionName;
        Assert.Equal("tmux attach -t " + target, fixture.Companion.AttachCommand);
        fixture.Companion.Ring();
        Assert.Equal(["send-keys", "-t", target + ":", "-l", AgentBriefing.Doorbell], fixture.Commands[^2]);
        Assert.Equal(["send-keys", "-t", target + ":", "Enter"], fixture.Commands[^1]);
        fixture.Companion.Stop();
        Assert.Equal(["kill-session", "-t", target], fixture.Commands[^1]);
    }

    [Fact]
    public async Task CleanupTargetsTheUniqueSessionAndItsCreationMarker()
    {
        using var fixture = new Fixture();
        var original = Assert.IsType<AgentOwnership>(await fixture.Companion.StartAsync(CancellationToken.None));
        Assert.Null(await fixture.Companion.StartAsync(CancellationToken.None));
        fixture.Companion.Stop();
        var replacement = Assert.IsType<AgentOwnership>(await fixture.Companion.StartAsync(CancellationToken.None));
        var creations = fixture.Commands.Where(args => args[0] == "new-session").ToArray();
        Assert.Equal(2, creations.Length);
        Assert.Equal(AgentBriefing.Text, File.ReadAllText(fixture.Paths.BriefingFile));
        var first = FakeProcessRunner.After(creations[0], "-e").Split('=')[1];
        var second = FakeProcessRunner.After(creations[1], "-e").Split('=')[1];
        Assert.True(Guid.TryParseExact(first, "N", out _));
        Assert.NotEqual(first, second);
        Assert.Equal("#{session_id}", FakeProcessRunner.After(creations[0], "-F"));
        Assert.Equal(["new-session", "-d", "-s", fixture.Companion.SessionName, "-c", fixture.Paths.Root,
            "-x", "200", "-y", "50", "-e", "MILLIGRAM_SESSION_OWNER=" + first, "-P", "-F", "#{session_id}",
            "bash " + AgentLaunches.BashQuote(Path.Combine(fixture.Paths.RunDirectory, "agent.sh"))], creations[0]);

        original.Stop();
        Assert.Equal(["if-shell", "-F", "-t", "$1", "#{==:#{MILLIGRAM_SESSION_OWNER}," + first + "}", "kill-session -t '$1'"], fixture.Commands[^1]);
        replacement.Stop();
        Assert.Equal(["if-shell", "-F", "-t", "$2", "#{==:#{MILLIGRAM_SESSION_OWNER}," + second + "}", "kill-session -t '$2'"], fixture.Commands[^1]);
    }

    [Theory]
    [InlineData("$0")]
    [InlineData("$9")]
    [InlineData("$109")]
    public async Task NumericSessionIdsIncludeTheirBoundaryDigits(string id)
    {
        using var fixture = new Fixture { Output = id };
        var ownership = Assert.IsType<AgentOwnership>(await fixture.Companion.StartAsync(CancellationToken.None));
        ownership.Stop();
        Assert.Equal(id, FakeProcessRunner.After(fixture.Commands[^1], "-t"));
    }

    [Theory]
    [InlineData(false, true, "", "agent.enabled")]
    [InlineData(true, false, "", "native Windows")]
    [InlineData(true, true, "tmux", "tmux is not installed.")]
    [InlineData(true, true, "copilot", "'copilot' is not on PATH.")]
    public async Task PrerequisiteFailuresNeverAttemptToCreateASession(bool enabled, bool supported, string missing, string diagnostic)
    {
        using var fixture = new Fixture(enabled, supported, missing);
        Assert.False(fixture.Companion.IsAvailable(out var reason));
        Assert.Contains(diagnostic, reason);
        Assert.Equal(reason, (await Assert.ThrowsAsync<MilligramException>(() => fixture.Companion.StartAsync(CancellationToken.None))).Message);
        Assert.DoesNotContain(fixture.Commands, args => args[0] == "new-session");
        if (!supported || missing == "tmux") Assert.Empty(fixture.Commands);
    }

    [Fact]
    public void TheDefaultPlatformCheckPreventsNativeWindowsFromUsingTmux()
    {
        using var project = new TempProject();
        var companion = new TmuxCompanion(new ProjectPaths(project.Root), () => new Policy(), ["milligram"])
        {
            OnPath = _ => false,
            Capture = _ => throw new InvalidOperationException("An unavailable tmux cannot be invoked."),
        };
        Assert.False(companion.IsRunning());
        Assert.False(companion.IsAvailable(out var reason));
        Assert.Contains(OperatingSystem.IsWindows() ? "native Windows" : "not installed", reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("$")]
    [InlineData("$1; kill-server")]
    [InlineData("$a")]
    [InlineData("not an id")]
    public async Task AnInvalidSessionIdCannotBecomeAnAutomaticStopCommand(string output)
    {
        using var fixture = new Fixture { Output = output };
        var error = await Assert.ThrowsAsync<MilligramException>(() => fixture.Companion.StartAsync(CancellationToken.None));
        Assert.Contains("did not identify", error.Message);
        Assert.DoesNotContain(fixture.Commands, args => args[0] == "if-shell");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFailedCreationOnlyReusesAnActuallyRunningSession(bool competing)
    {
        using var fixture = new Fixture { CreateCode = 1, Competing = competing };
        if (competing) Assert.Null(await fixture.Companion.StartAsync(CancellationToken.None));
        else Assert.Contains("could not start", (await Assert.ThrowsAsync<MilligramException>(() => fixture.Companion.StartAsync(CancellationToken.None))).Message);
        Assert.DoesNotContain(fixture.Commands, args => args[0] is "kill-session" or "if-shell");
    }

    [Fact]
    public async Task CancellationCannotCreateASession()
    {
        using var fixture = new Fixture();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Companion.StartAsync(new CancellationToken(true)));
        Assert.Empty(fixture.Commands);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TempProject project = new();
        private bool running;
        private int sessions;
        public string? Output { get; init; }
        public int CreateCode { get; init; }
        public bool Competing { get; init; }
        public List<string[]> Commands { get; } = [];
        public ProjectPaths Paths { get; }
        public TmuxCompanion Companion { get; }
        public Fixture(bool enabled = true, bool supported = true, string missing = "")
        {
            Paths = new ProjectPaths(project.Root);
            Companion = new TmuxCompanion(Paths, () => new Policy { Agent = new AgentSettings { Enabled = enabled, Terminal = "none" } }, ["milligram"])
            {
                Supported = supported,
                OnPath = name => !string.Equals(name, missing, StringComparison.Ordinal),
                Capture = args =>
                {
                    Commands.Add(args);
                    switch (args[0])
                    {
                        case "has-session": Assert.Equal(["has-session", "-t", "=" + Companion!.SessionName], args); return (running ? 0 : 1, "");
                        case "new-session": running = CreateCode == 0 || Competing; return (CreateCode, Output ?? $"${++sessions}\n");
                        case "kill-session": running = false; return (0, "");
                        case "if-shell": return (0, "");
                        case "send-keys": return (0, "");
                        default: throw new InvalidOperationException("Unexpected tmux command.");
                    }
                },
            };
        }
        public void Dispose() => project.Dispose();
    }
}
