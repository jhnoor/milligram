using System.Collections.Concurrent;
using Milligram.Adapters.Processes;

namespace Milligram.Tests.Adapters;

public class ProcessRunnerTests
{
    [Fact]
    public async Task StreamingCleansLinesAndKeepsTheNonzeroExitCode()
    {
        using var project = new TempProject();
        var (command, args) = Script(project);
        var lines = new ConcurrentQueue<string>();
        Assert.Equal(17, await new ProcessRunner().RunAsync(command, args, project.Root, lines.Enqueue, CancellationToken.None));
        Assert.Equal(["colored", "error", "out"], lines.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void CaptureKeepsRawOutputAndCombinesStdoutBeforeStderr()
    {
        using var project = new TempProject();
        var (command, args) = Script(project);
        var (code, output) = ProcessRunner.Capture(command, args);
        Assert.Equal(17, code);
        Assert.Equal("out\n\u001b[31mcolored\u001b[0m   \n\nerror\n", output.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ALogCallbackFailureIsReturnedToTheCaller()
    {
        using var project = new TempProject();
        var (command, args) = Script(project);
        var failure = new InvalidOperationException("log failed");
        var lines = new ConcurrentQueue<string>();
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ProcessRunner().RunAsync(command, args, project.Root, line => { lines.Enqueue(line); throw failure; }, CancellationToken.None)));
        Assert.DoesNotContain(lines, line => line.StartsWith("Stopped waiting for redirected output", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ManyLinesInOneWriteRemainSeparateLogEntries()
    {
        using var project = new TempProject();
        var expected = Enumerable.Range(0, 2000).Select(i => "line " + i).ToArray();
        var text = project.Write("lines.txt", string.Join('\n', expected) + "\n");
        var command = OperatingSystem.IsWindows() ? project.Write("lines.cmd", "@echo off\r\ntype \"%~dp0lines.txt\"\r\n") : "/bin/cat";
        var lines = new List<string>();
        Assert.Equal(0, await new ProcessRunner().RunAsync(command, OperatingSystem.IsWindows() ? [] : [text],
            project.Root, lines.Add, CancellationToken.None));
        Assert.Equal(expected, lines);
    }

    [Fact]
    public async Task MissingExecutablesHaveAnActionableDiagnosticAndExit127()
    {
        using var project = new TempProject();
        var missing = Path.Combine(project.Root, "not-installed");
        var lines = new List<string>();
        Assert.Equal(127, await new ProcessRunner().RunAsync(missing, [], project.Root, lines.Add, CancellationToken.None));
        Assert.Contains(missing, Assert.Single(lines));
        var result = ProcessRunner.Capture(missing);
        Assert.Equal(127, result.ExitCode);
        Assert.NotEmpty(result.Output);
    }

    [Fact]
    public async Task AnAlreadyCancelledJobDoesNotTryToStartAProcess()
    {
        using var project = new TempProject();
        var lines = new List<string>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ProcessRunner().RunAsync(
            Path.Combine(project.Root, "not-installed"), [], project.Root, lines.Add, new CancellationToken(true)));
        Assert.Empty(lines);
    }

    [UnixFact]
    public async Task ClosingBothOutputStreamsStillWaitsForTheCommandsExitCode()
    {
        using var project = new TempProject();
        var script = project.Write("close.sh", "exec 1>&- 2>&-\nsleep 0.1\nexit 17\n");
        Assert.Equal(17, await new ProcessRunner().RunAsync("/bin/sh", [script], project.Root, _ => { }, CancellationToken.None));
    }

    private static (string Command, string[] Args) Script(TempProject project)
    {
        if (OperatingSystem.IsWindows()) return (project.Write("command.cmd",
            "@echo off\r\necho out\r\necho \u001b[31mcolored\u001b[0m   \r\necho(\r\n1>&2 echo error\r\nexit /b 17\r\n"), []);
        return ("/bin/sh", [project.Write("command.sh",
            "printf 'out\n\u001b[31mcolored\u001b[0m   \n\n'\nprintf 'error\n' >&2\nexit 17\n")]);
    }
}
