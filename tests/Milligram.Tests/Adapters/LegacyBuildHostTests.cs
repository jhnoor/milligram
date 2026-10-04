using Milligram.Adapters.Processes;

namespace Milligram.Tests.Adapters;

public class LegacyBuildHostTests
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
        var warning = LegacyBuildHost.PathError(directory, windows);
        if (!expected) Assert.Null(warning);
        else
        {
            Assert.Contains("shorter --tool-path", warning!, StringComparison.Ordinal);
            Assert.Contains($"({length} characters)", warning, StringComparison.Ordinal);
            Assert.Contains(Path.Combine(directory, suffix), warning, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("<Project/>", true)]
    [InlineData("<Project xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\"><PropertyGroup><TargetFrameworkVersion>v4.8</TargetFrameworkVersion></PropertyGroup></Project>", true)]
    [InlineData("<Project Sdk=\"Microsoft.NET.Sdk\"/>", false)]
    [InlineData("<Project><Import Sdk=\"Microsoft.NET.Sdk\" Project=\"Sdk.props\"/></Project>", false)]
    [InlineData("<Project><Sdk Name=\"Microsoft.NET.Sdk\"/></Project>", false)]
    [InlineData("<Project><PropertyGroup><TargetFramework>net48</TargetFramework></PropertyGroup></Project>", false)]
    [InlineData("<Project><PropertyGroup><TargetFrameworks>net48;net10.0</TargetFrameworks></PropertyGroup></Project>", false)]
    [InlineData("<Project><PropertyGroup>", false)]
    [InlineData("<!DOCTYPE Project [<!ENTITY value 'not loaded'>]><Project>&value;</Project>", false)]
    public void DeepPathsRejectOnlyProjectsThatRoslynSendsToTheFrameworkHost(string xml, bool legacy)
    {
        using var project = new TempProject(("App.csproj", xml));
        var file = Path.Combine(project.Root, "App.csproj");
        var directory = new string('x', 260);
        if (legacy)
        {
            var error = Assert.Throws<InvalidOperationException>(() => LegacyBuildHost.Check(directory, true, [file]));
            Assert.Contains(file, error.Message, StringComparison.Ordinal);
            Assert.Contains("shorter --tool-path", error.Message, StringComparison.Ordinal);
        }
        else LegacyBuildHost.Check(directory, true, [file]);
        LegacyBuildHost.Check(directory, false, [file]);
        LegacyBuildHost.Check("short", true, [file]);
    }

    [Fact]
    public void AReferencedLegacyProjectIsCheckedEvenWhenTheEntryUsesTheSdk()
    {
        using var project = new TempProject(("A.Modern.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"/>"), ("Z.Legacy.csproj", "<Project/>"));
        var modern = Path.Combine(project.Root, "A.Modern.csproj");
        var legacy = Path.Combine(project.Root, "Z.Legacy.csproj");
        var error = Assert.Throws<InvalidOperationException>(() => LegacyBuildHost.Check(new string('x', 260), true, [modern, legacy]));
        Assert.Contains(legacy, error.Message, StringComparison.Ordinal);
    }

}
