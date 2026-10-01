using Milligram.Adapters.Cli;
using Milligram.Application;

namespace Milligram.Tests.Adapters;

public class CommandLineTests
{
    [Theory]
    [InlineData("serve")]
    [InlineData("init")]
    [InlineData("ir")]
    public void ScanCommandsShareConfigurationAndAnExplicitSourceOnlyFallback(string command)
    {
        var configured = CommandLine.Parse([command, "--configuration", "Release"]).ScanOptions()!;
        Assert.Equal(Milligram.Domain.Policies.ScanMode.Auto, configured.Mode);
        Assert.Equal("Release", configured.Configuration);
        Assert.Empty(configured.Projects);
        var explicitProject = CommandLine.Parse([command, "--msbuild", "App.csproj"]).ScanOptions()!;
        Assert.Equal(Milligram.Domain.Policies.ScanMode.Msbuild, explicitProject.Mode);
        Assert.Equal(["App.csproj"], explicitProject.Projects);
        var source = CommandLine.Parse(["--source-only", command]);
        Assert.Equal(command, source.Command);
        Assert.Equal(Milligram.Domain.Policies.ScanMode.SourceOnly, source.ScanOptions()!.Mode);
        Assert.Throws<MilligramException>(() => CommandLine.Parse([command, "--source-only", "--msbuild", "App.csproj"]).ScanOptions());
    }

    [Fact]
    public void ProjectScansKeepRepeatedEntryFilesAndTheSelectedConfiguration()
    {
        var line = CommandLine.Parse(["ir", "--msbuild", "src/App.csproj", "--configuration", "Release", "--msbuild=Library.csproj"]);
        Assert.Equal(["src/App.csproj", "Library.csproj"], line.ScanOptions()!.Projects);
        Assert.Equal("Release", line.Value("configuration"));
        Assert.Empty(line.Arguments);
        Assert.Null(CommandLine.Parse(["ir"]).ScanOptions());
        Assert.Single(CommandLine.Parse(["ir", "--msbuild", "App.csproj"]).ScanOptions()!.Projects);
    }

    [Theory]
    [InlineData("doctor --msbuild App.csproj", "Scan options are supported")]
    [InlineData("ir --source-only --configuration Release", "cannot be combined")]
    [InlineData("ir --msbuild", "requires a C# project file")]
    [InlineData("ir --msbuild --configuration Release", "requires a C# project file")]
    [InlineData("ir --msbuild App.csproj --msbuild=", "requires a C# project file")]
    [InlineData("ir --msbuild App.csproj --msbuild --configuration Release", "requires a C# project file")]
    [InlineData("ir --msbuild App.csproj --configuration", "requires a configuration name")]
    [InlineData("ir --msbuild App.csproj --configuration --no-agent", "requires a configuration name")]
    public void ProjectScanOptionsCannotSilentlyUseTheSourceOnlyScanner(string command, string explanation) =>
        Assert.Contains(explanation, Assert.Throws<MilligramException>(() => CommandLine.Parse(command.Split(' ')).ScanOptions()).Message, StringComparison.Ordinal);

    [Theory]
    [InlineData("agent", "host", "instance-id")]
    [InlineData("agent", "start", null)]
    [InlineData("serve", "host", null)]
    public void OnlyTheInternalHostReceivesTheLaunchInstance(string command, string subcommand, string? expected)
    {
        var line = CommandLine.Parse([command, subcommand, "--project", "with spaces", "--instance", "instance-id"]);
        Assert.Equal(expected, line.HostInstance);
        Assert.Equal([subcommand], line.Arguments);
        Assert.Equal("with spaces", line.Value("project"));
    }

    [Theory]
    [InlineData("agent", true)]
    [InlineData("agent status", true)]
    [InlineData("agent start", true)]
    [InlineData("agent stop", true)]
    [InlineData("agent attach", true)]
    [InlineData("agent host", true)]
    [InlineData("agent typo", false)]
    [InlineData("agent start stop", false)]
    [InlineData("agent host extra", false)]
    [InlineData("serve", false)]
    public void AgentCommandsRejectUnknownOrExtraSubcommands(string command, bool valid) =>
        Assert.Equal(valid, CommandLine.Parse(command.Split(' ')).IsAgentCommand);

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
    [InlineData("1", 1)]
    [InlineData("6000", 6000)]
    [InlineData("65535", 65535)]
    public void PortsAcceptBothBoundariesAndOnlyDefaultWhenOmitted(string value, int expected)
    {
        Assert.Equal(expected, CommandLine.Parse(["--port", value]).Port(5170));
        Assert.Equal(5170, CommandLine.Parse([]).Port(5170));
    }

    [Theory]
    [InlineData("")]
    [InlineData("invalid")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("65536")]
    [InlineData("2147483648")]
    [InlineData("--no-agent")]
    public void InvalidPortsAreErrorsRatherThanAnUnrequestedDefault(string value) =>
        Assert.Equal("--port must be an integer from 1 to 65535.",
            Assert.Throws<MilligramException>(() => CommandLine.Parse(["--port", value]).Port(5170)).Message);

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
