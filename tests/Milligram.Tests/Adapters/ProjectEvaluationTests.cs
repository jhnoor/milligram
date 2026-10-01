using Milligram.Adapters.Processes;

namespace Milligram.Tests.Adapters;

public class ProjectEvaluationTests
{
    [Theory]
    [InlineData(259, true, false)]
    [InlineData(260, true, true)]
    [InlineData(264, true, true)]
    [InlineData(264, false, false)]
    public void DeepWindowsInstallationsExplainHowToStartTheLegacyHost(int length, bool windows, bool expected)
    {
        var suffix = Path.Combine("BuildHost-net472", "Microsoft.CodeAnalysis.Workspaces.MSBuild.BuildHost.exe.config");
        var directory = new string('x', length - suffix.Length - 1);
        var warning = ProcessRunner.LegacyHostPathWarning(directory, windows);
        if (!expected) Assert.Null(warning);
        else
        {
            Assert.Contains("shorter --tool-path", warning!, StringComparison.Ordinal);
            Assert.Contains($"({length} characters)", warning, StringComparison.Ordinal);
            Assert.Contains(Path.Combine(directory, suffix), warning, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("Release", "Release")]
    [InlineData("Team Debug å", "Team Debug å")]
    [InlineData("A;B,C%$@()'?*", "A%3BB%2CC%25%24%40%28%29%27%3F%2A")]
    public void ConfigurationNamesRemainOneLiteralMsBuildProperty(string name, string expected) =>
        Assert.Equal(expected, ProcessRunner.EscapeProperty(name));
}
