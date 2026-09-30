using System.Diagnostics;
using Milligram.Adapters.Processes;

namespace Milligram.Tests.Adapters;

public class DetachedProcessTests
{
    [Fact]
    public void ADetachedHostGetsSeparateStandardHandlesAndNoVisibleWindow()
    {
        using var project = new TempProject();
        var args = new[] { "agent", "host", "--project", project.Root + " & 漢" };
        var info = ProcessRunner.DetachedStartInfo("milligram-fixture", args, project.Root);
        Assert.Equal("milligram-fixture", info.FileName);
        Assert.Equal(args, info.ArgumentList);
        Assert.Equal(project.Root, info.WorkingDirectory);
        Assert.True(info.RedirectStandardInput);
        Assert.True(info.RedirectStandardOutput);
        Assert.True(info.RedirectStandardError);
        Assert.True(info.CreateNoWindow);
        Assert.Equal(ProcessWindowStyle.Hidden, info.WindowStyle);
        Assert.False(info.UseShellExecute);
    }

    [WindowsFact]
    public void DetachedBatchLaunchesRetainTheExistingCmdQuoting()
    {
        using var project = new TempProject();
        var command = project.Write("tool path/milligram.cmd", "@echo off\r\n");
        var args = new[] { "agent", "host", "--project", project.Root + " & tail" };
        var info = ProcessRunner.DetachedStartInfo(command, args, project.Root);
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "cmd.exe"), info.FileName);
        Assert.Equal(BatchCommandLine.For(command, args), info.Arguments);
        Assert.Empty(info.ArgumentList);
    }
}
