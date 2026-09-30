using Milligram.Adapters.Companion;
using Milligram.Application;

namespace Milligram.Tests.Adapters;

public class NativeTerminalCommandTests
{
    [Theory]
    [InlineData("auto")]
    [InlineData("none")]
    public void PanelAndManualAttachmentDoNotOpenExternalWindows(string template) =>
        Assert.Null(NativeTerminalCommand.Parse(template, ["milligram"], "/project"));

    [Theory]
    [InlineData("gnome-terminal -- {command}", "gnome-terminal", new[] { "--" })]
    [InlineData("wezterm start -- {command}", "wezterm", new[] { "start", "--" })]
    [InlineData("terminal --title \"\" {command}", "terminal", new[] { "--title", "" })]
    [InlineData("terminal\t--\t{command}", "terminal", new[] { "--" })]
    [InlineData("  terminal  --  {command}  ", "terminal", new[] { "--" })]
    [InlineData("\"/terminal with spaces\" --title \"Agent window\" \"{command}\"", "/terminal with spaces", new[] { "--title", "Agent window" })]
    public void TheSameBuildAndExplicitProjectRemainSeparateArguments(string template, string command, string[] prefix)
    {
        string[] self = ["/runtime with spaces/dotnet", "/build & \"漢\"/Milligram.dll"];
        const string root = "/project with ' and ; & 漢";
        var launch = NativeTerminalCommand.Parse(template, self, root)!;
        Assert.Equal(command, launch.Command);
        Assert.Equal([.. prefix, .. self, "agent", "attach", "--project", root], launch.Args);
    }

    [Theory]
    [InlineData("wt")]
    [InlineData("wt.exe")]
    [InlineData("C:\\Program Files\\Terminal\\WT.EXE")]
    public void WindowsTerminalCannotSplitProjectOrBuildPathsIntoNewCommands(string command)
    {
        var launch = NativeTerminalCommand.Parse($"\"{command}\" -w 0 new-tab {{command}}",
            [@"C:\runtime;one\dotnet.exe", @"C:\build\;two\Milligram.dll"], @"C:\project & 漢;three")!;
        Assert.Equal(command, launch.Command);
        Assert.Equal(["-w", "0", "new-tab", @"C:\runtime\;one\dotnet.exe", @"C:\build\\;two\Milligram.dll",
            "agent", "attach", "--project", @"C:\project & 漢\;three"], launch.Args);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"\" {command}")]
    [InlineData("\" \" {command}")]
    [InlineData("{command}")]
    [InlineData("wt")]
    [InlineData("wt {}")]
    [InlineData("wt x{command}")]
    [InlineData("wt {command} tail")]
    [InlineData("wt {command} {command}")]
    [InlineData("wt {session} {command}")]
    [InlineData("wt \"{command}")]
    [InlineData("wt --title \"{command}\" {command}")]
    public void AnAmbiguousTemplateExplainsTheWholeArgumentPlaceholder(string? template)
    {
        var error = Assert.Throws<MilligramException>(() => NativeTerminalCommand.Parse(template, ["milligram"], "/project"));
        Assert.Contains("agent.terminal", error.Message);
        Assert.Contains("separate {command}", error.Message);
    }
}
