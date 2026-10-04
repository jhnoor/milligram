using Milligram.Adapters.Processes;

namespace Milligram.Tests.Adapters;

public class BuildEnvironmentTests
{
    [Fact]
    public void ChildToolsKeepTheCallersBuildSettingsAndOtherEnvironmentVariables()
    {
        var environment = new BuildEnvironment(new Dictionary<string, string?>
        {
            ["MSBUILD_EXE_PATH"] = "custom/MSBuild.exe",
            ["MSBuildExtensionsPath"] = "custom/extensions",
            ["MSBuildSDKsPath"] = "custom/Sdks",
        });
        var child = SdkEnvironment();
        environment.Restore(child);
        Assert.Equal("custom/MSBuild.exe", child["MSBUILD_EXE_PATH"]);
        Assert.Equal("custom/extensions", child["MSBuildExtensionsPath"]);
        Assert.Equal("custom/Sdks", child["MSBuildSDKsPath"]);
        Assert.Equal("original/path", child["PATH"]);
    }

    [Fact]
    public void AnUnconfiguredCallerLeavesChildToolsFreeToDiscoverTheirOwnMsBuild()
    {
        var environment = new BuildEnvironment(new Dictionary<string, string?>
        {
            ["MSBUILD_EXE_PATH"] = null,
        });
        var child = SdkEnvironment();
        environment.Restore(child);
        Assert.False(child.ContainsKey("MSBUILD_EXE_PATH"));
        Assert.False(child.ContainsKey("MSBuildExtensionsPath"));
        Assert.False(child.ContainsKey("MSBuildSDKsPath"));
        Assert.Equal("original/path", Assert.Single(child).Value);
    }

    private static Dictionary<string, string?> SdkEnvironment() => new(StringComparer.Ordinal)
    {
        ["MSBUILD_EXE_PATH"] = "scanner/MSBuild.dll",
        ["MSBuildExtensionsPath"] = "scanner/extensions",
        ["MSBuildSDKsPath"] = "scanner/Sdks",
        ["PATH"] = "original/path",
    };
}
