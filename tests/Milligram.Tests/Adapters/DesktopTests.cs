using Milligram.Adapters.Companion;

namespace Milligram.Tests.Adapters;

public class DesktopTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AutomaticTerminalsAttachToTheExactSessionAndReportLaunchFailure(bool opened)
    {
        foreach (var (terminal, flag) in new[] { ("x-terminal-emulator", "-e"), ("gnome-terminal", "--"), ("konsole", "-e"), ("xterm", "-e") })
        {
            var calls = new List<(string Command, IReadOnlyList<string> Args, string? Root)>();
            var result = Desktop.OpenTerminal("auto", "milligram-漢字-12345678", "project with spaces",
                command => command == terminal || command == "wt.exe", (command, args, root) => { calls.Add((command, args, root)); return opened; });
            Assert.Equal(opened, result);
            var call = Assert.Single(calls);
            if (Desktop.IsWsl)
            {
                Assert.Equal("wt.exe", call.Command);
                Assert.Equal(["-w", "milligram", "new-tab", "--title", "Milligram agent", "wsl.exe", "-d",
                    Environment.GetEnvironmentVariable("WSL_DISTRO_NAME")!, "--", "tmux", "attach", "-t", "=milligram-漢字-12345678"], call.Args);
                Assert.Equal("/mnt/c", call.Root);
            }
            else if (OperatingSystem.IsMacOS())
            {
                Assert.Equal("osascript", call.Command);
                Assert.Equal(["-e", "tell application \"Terminal\" to do script \"tmux attach -t =milligram-漢字-12345678\""], call.Args);
                Assert.Null(call.Root);
            }
            else
            {
                Assert.Equal(terminal, call.Command);
                Assert.Equal([flag, "tmux", "attach", "-t", "=milligram-漢字-12345678"], call.Args);
                Assert.Equal("project with spaces", call.Root);
            }
        }
    }

    [Fact]
    public void UnavailableAutomaticTerminalsLeaveAttachmentManual() =>
        Assert.False(Desktop.OpenTerminal("auto", "session", "project", _ => false, (_, _, _) => false));

    [Fact]
    public void CustomTemplatesKeepTheRawSessionNameAndQuotedArgumentBoundaries()
    {
        var launched = false;
        Assert.True(Desktop.OpenTerminal("terminal \"argument with spaces\" -t ={session} --title {session}", "milligram-project-12345678", "project",
            _ => throw new InvalidOperationException("Custom templates launch their own executable."), (command, args, root) =>
            {
                launched = true;
                Assert.Equal("terminal", command);
                Assert.Equal(["argument with spaces", "-t", "=milligram-project-12345678", "--title", "milligram-project-12345678"], args);
                Assert.Equal("project", root);
                return true;
            }));
        Assert.True(launched);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("")]
    [InlineData("   ")]
    public void DisabledAndEmptyTemplatesCannotOpenAWindow(string template) =>
        Assert.False(Desktop.OpenTerminal(template, "session", "project",
            _ => throw new InvalidOperationException("No executable should be looked up."),
            (_, _, _) => throw new InvalidOperationException("No terminal should be launched.")));
}
