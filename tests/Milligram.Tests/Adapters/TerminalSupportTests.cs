using Milligram.Adapters.Processes;

namespace Milligram.Tests.Adapters;

public class TerminalSupportTests
{
    [Theory]
    [InlineData("linux-x64")]
    [InlineData("linux-arm64")]
    [InlineData("osx-x64")]
    [InlineData("osx-arm64")]
    [InlineData("win-x64")]
    [InlineData("win-arm64")]
    public void EveryTargetRuntimeHasTerminalSupport(string rid) =>
        Assert.Null(ProcessRunner.TerminalPlatformIssue(rid, new Version(10, 0, 17763)));

    [Theory]
    [InlineData("linux-musl-x64")]
    [InlineData("linux-arm")]
    [InlineData("win-x86")]
    [InlineData("freebsd-x64")]
    public void UnsupportedRuntimesGetAnAlternativeInsteadOfANativeLoaderCrash(string rid)
    {
        var reason = ProcessRunner.TerminalPlatformIssue(rid, new Version(10, 0, 19045));
        Assert.Contains(rid, reason);
        Assert.Contains("tmux", reason);
    }

    [Theory]
    [InlineData("win-x64")]
    [InlineData("win-arm64")]
    public void WindowsBeforeConptyGetsAnUpgradeMessage(string rid) =>
        Assert.Contains("1809", ProcessRunner.TerminalPlatformIssue(rid, new Version(10, 0, 17762)));
}
