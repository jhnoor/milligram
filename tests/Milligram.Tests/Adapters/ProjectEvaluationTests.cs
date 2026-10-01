using Milligram.Adapters.Processes;

namespace Milligram.Tests.Adapters;

public class ProjectEvaluationTests
{
    [Theory]
    [InlineData("Release", "Release")]
    [InlineData("Team Debug å", "Team Debug å")]
    [InlineData("A;B,C%$@()'?*", "A%3BB%2CC%25%24%40%28%29%27%3F%2A")]
    public void ConfigurationNamesRemainOneLiteralMsBuildProperty(string name, string expected) =>
        Assert.Equal(expected, ProcessRunner.EscapeProperty(name));
}
