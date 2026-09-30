using Milligram.Application;

namespace Milligram.Tests.Main;

public class ProgramTests
{
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("65536")]
    [InlineData("invalid")]
    [InlineData("")]
    public async Task InvalidPortsDoNotInitializeTheProject(string port)
    {
        using var project = new TempProject(("Source.cs", "namespace Example; public class Source {}"));
        var result = await global::Milligram.Main.Program.Main(["--project", project.Root, "--port=" + port, "--no-agent", "--no-browser"]);
        var paths = new ProjectPaths(project.Root);
        Assert.Equal(1, result);
        Assert.False(File.Exists(paths.PolicyFile));
        Assert.False(Directory.Exists(paths.StateDirectory));
    }
}
