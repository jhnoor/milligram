using Milligram.Adapters.Cli;

namespace Milligram.Tests.Adapters;

public class CommandLineTests
{
    [Theory]
    [InlineData("no-agent")]
    [InlineData("no-browser")]
    [InlineData("all")]
    [InlineData("peek")]
    [InlineData("force")]
    [InlineData("keep-agent")]
    [InlineData("help")]
    [InlineData("version")]
    [InlineData("json")]
    public void FlagsNeverConsumeTheFollowingPositionalArgument(string flag)
    {
        var line = CommandLine.Parse(["agent", "--" + flag, "host"]);
        Assert.True(line.Has(flag));
        Assert.Equal("", line.Value(flag));
        Assert.Equal("host", line.Subcommand);
    }

    [Fact]
    public void ShortHelpAndAnOptionAtTheEndDoNotReadPastTheArgumentList()
    {
        Assert.True(CommandLine.Parse(["-h"]).Has("help"));
        var line = CommandLine.Parse(["agent", "start", "--project"]);
        Assert.True(line.Has("project"));
        Assert.Equal("", line.Value("project"));
        Assert.Equal(["start"], line.Arguments);
        Assert.Null(line.Value("absent"));
    }

    [Theory]
    [InlineData("6000", 6000)]
    [InlineData("invalid", 5170)]
    public void NumericOptionsUseTheirFallbackOnlyWhenTheyCannotBeParsed(string value, int expected)
    {
        Assert.Equal(expected, CommandLine.Parse(["--port", value]).IntValue("port", 5170));
        Assert.Equal(5170, CommandLine.Parse([]).IntValue("port", 5170));
    }

    [Fact]
    public void AGlobalProjectOptionDoesNotConsumeTheAgentSubcommand()
    {
        var line = CommandLine.Parse(["--project", "project with spaces", "agent", "host"]);
        Assert.Equal("agent", line.Command);
        Assert.Equal("host", line.Subcommand);
        Assert.Equal(["host"], line.Arguments);
        Assert.Equal("project with spaces", line.Value("project"));
        Assert.Null(CommandLine.Parse(["agent"]).Subcommand);
    }
}
