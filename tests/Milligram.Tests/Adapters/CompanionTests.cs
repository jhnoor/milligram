using Milligram.Adapters.Companion;
using Milligram.Adapters.Processes;
using Milligram.Application;
using Milligram.Domain.Policies;

namespace Milligram.Tests.Adapters;

public class AgentLaunchTests : IDisposable
{
    private readonly TempProject project = new();
    private readonly ProjectPaths paths;
    private Policy policy = new();

    public AgentLaunchTests() => paths = new ProjectPaths(project.Root);

    public void Dispose() => project.Dispose();

    private AgentLaunches Launches(params string[] self) => new(paths, () => policy, self.Length > 0 ? self : ["/usr/bin/dotnet", "/opt/milligram/Milligram.dll"]);

    private static readonly AgentConversation Conversation = new("s-1", DateTimeOffset.UnixEpoch);

    [Fact]
    public void ANewCopilotConversationIsNamedAndBriefed()
    {
        var args = AgentLaunches.CopilotArgs(new AgentSettings(), Conversation, resumed: false, paths);

        Assert.Equal(
            [
                "--session-id", "s-1", "--name", $"Milligram: {Path.GetFileName(project.Root)}",
                "--allow-tool", "shell(milligram:*)", "--allow-tool", $"write({paths.PolicyFile})",
                "-i", AgentBriefing.LaunchPrompt,
            ],
            args);
    }

    [Fact]
    public void AResumedCopilotConversationIsToldToRereadItsBriefing()
    {
        var args = AgentLaunches.CopilotArgs(new AgentSettings(), Conversation, resumed: true, paths);

        Assert.DoesNotContain("--name", args);
        Assert.Equal(["-i", AgentBriefing.ResumePrompt], args.TakeLast(2));
    }

    [Fact]
    public void AllowedToolsTheModelAndExtraArgumentsComeBeforeThePrompt()
    {
        var settings = new AgentSettings { AllowTools = ["shell(git:*)", "write"], Model = "gpt-6", Args = ["--no-color"] };

        var args = AgentLaunches.CopilotArgs(settings, Conversation, resumed: true, paths);

        Assert.Equal(
            ["--session-id", "s-1", "--allow-tool", "shell(git:*)", "--allow-tool", "write", "--allow-tool", $"write({paths.PolicyFile})",
             "--model", "gpt-6", "--no-color", "-i", AgentBriefing.ResumePrompt],
            args);
    }

    [Theory]
    [InlineData("copilot", true)]
    [InlineData("/home/me/.local/bin/copilot", true)]
    [InlineData(@"C:\Users\me\AppData\Roaming\npm\copilot.cmd", true)]
    [InlineData("copilot.exe", true)]
    [InlineData("my-agent", false)]
    [InlineData("/opt/copilot/agent", false)]
    public void CopilotIsRecognisedByNameWhereverItIs(string command, bool copilot) => Assert.Equal(copilot, AgentLaunches.IsCopilot(command));

    [Fact]
    public void AnotherAgentGetsOnlyItsOwnArguments()
    {
        policy = new Policy { Agent = new AgentSettings { Command = "my-agent", Args = ["--fast"] } };

        var launch = Launches().Prepare(windows: false);

        Assert.Equal("my-agent", launch.Command);
        Assert.Equal(["--fast"], launch.Args);
        Assert.Equal(paths.Root, launch.WorkingDirectory);
    }

    [Fact]
    public void TheConversationIsCreatedOnceAndResumedAfterwards()
    {
        var first = Launches().Prepare(windows: false);
        var second = Launches().Prepare(windows: false);

        var sessionId = FakeProcessRunner.After(first.Args, "--session-id");
        Assert.Equal(sessionId, JsonFile.Read<AgentConversation>(paths.AgentStateFile)!.SessionId);
        Assert.Equal(sessionId, FakeProcessRunner.After(second.Args, "--session-id"));
        Assert.Contains("--name", first.Args);
        Assert.DoesNotContain("--name", second.Args);
        Assert.Equal(AgentBriefing.ResumePrompt, second.Args[^1]);
    }

    [Fact]
    public void TheShimIsWrittenIntoTheRunDirectoryForTheOs()
    {
        var unix = Launches().Prepare(windows: false);
        var windows = Launches().Prepare(windows: true);

        Assert.Equal(Path.Combine(paths.RunDirectory, "bin"), unix.ShimDirectory);
        Assert.Equal(AgentLaunches.Shim(["/usr/bin/dotnet", "/opt/milligram/Milligram.dll"], windows: false).Text, File.ReadAllText(Path.Combine(unix.ShimDirectory, "milligram")));
        Assert.True(File.Exists(Path.Combine(windows.ShimDirectory, "milligram.cmd")));
        if (!OperatingSystem.IsWindows())
            Assert.True(File.GetUnixFileMode(Path.Combine(unix.ShimDirectory, "milligram")).HasFlag(UnixFileMode.UserExecute));
    }

    [Fact]
    public void TheUnixShimQuotesEachWordForBash() =>
        Assert.Equal(("milligram", "#!/usr/bin/env bash\nexec '/usr/bin/dotnet' '/home/o'\\''brien/Milligram.dll' \"$@\"\n"),
            AgentLaunches.Shim(["/usr/bin/dotnet", "/home/o'brien/Milligram.dll"], windows: false));

    [Fact]
    public void TheWindowsShimQuotesEachWordAndDoublesPercentSigns() =>
        Assert.Equal(("milligram.cmd", "@echo off\r\n\"C:\\Program Files\\dotnet\\dotnet.exe\" \"C:\\100%%\\Milligram.dll\" %*\r\n"),
            AgentLaunches.Shim([@"C:\Program Files\dotnet\dotnet.exe", @"C:\100%\Milligram.dll"], windows: true));

    [Fact]
    public void ThePathPutsTheShimFirstWithTheOsSeparator()
    {
        var launch = new AgentLaunch("copilot", [], "/p", "/p/.milligram/run/bin");

        Assert.Equal("/p/.milligram/run/bin:/usr/bin", launch.PathFor("/usr/bin", ':'));
        Assert.Equal(@"/p/.milligram/run/bin;C:\bin", launch.PathFor(@"C:\bin", ';'));
        Assert.Equal("/p/.milligram/run/bin", launch.PathFor(null, ':'));
    }

    [Fact]
    public void TmuxRunsTheLaunchAsABashScript()
    {
        var launch = new AgentLaunch("copilot", ["--session-id", "s-1", "-i", "Read it's briefing"], "/work/my shop", "/work/my shop/.milligram/run/bin");

        Assert.Equal(
            """
            #!/usr/bin/env bash
            # Written by milligram: starts the companion agent inside tmux.
            export PATH='/work/my shop/.milligram/run/bin':"$PATH"
            cd '/work/my shop' || exit 1
            exec 'copilot' '--session-id' 's-1' '-i' 'Read it'\''s briefing'

            """,
            TmuxCompanion.Script(launch));
    }

    [WindowsFact]
    public void TheWindowsShimPassesItsArgumentsOn()
    {
        var target = project.Write(@"tool\echo args.cmd", "@echo off\r\necho(%*\r\n");
        var launch = Launches(target).Prepare(windows: true);

        var (exitCode, output) = ProcessRunner.Capture(Path.Combine(launch.ShimDirectory, "milligram"), "tell", "notify", "Moved Parser");

        Assert.Equal(0, exitCode);
        Assert.Equal("tell notify \"Moved Parser\"", output.Trim());
    }
}
