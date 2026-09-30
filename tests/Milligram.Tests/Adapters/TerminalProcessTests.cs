using System.Diagnostics;
using Milligram.Adapters.Companion;
using Milligram.Adapters.Processes;
using Porta.Pty;

namespace Milligram.Tests.Adapters;

public class TerminalProcessTests
{
    [Fact]
    public void ExecutableArgumentsAndWorkingDirectoryStaySeparateFromTheShell()
    {
        var launch = new AgentLaunch("copilot", ["a \"quote\" & 漢", ""], "/project with spaces", "/shim");
        var info = new ProcessStartInfo("/resolved/copilot", launch.Args) { WorkingDirectory = launch.WorkingDirectory };
        info.Environment["PATH"] = "inherited path";
        var options = ProcessRunner.TerminalOptions(info, launch, new TerminalSize(96, 31), true, "C.UTF-8");
        Assert.Equal("/resolved/copilot", options.App);
        Assert.Equal(launch.Args, options.CommandLine);
        Assert.False(options.VerbatimCommandLine);
        Assert.Equal(launch.WorkingDirectory, options.Cwd);
        Assert.Equal(96, options.Cols);
        Assert.Equal(31, options.Rows);
        Assert.True(options.UseAsyncIo);
        Assert.Equal("/shim" + Path.PathSeparator + "inherited path", options.Environment["PATH"]);
        Assert.Equal("xterm-256color", options.Environment["TERM"]);
        Assert.Equal("C.UTF-8", options.Environment["LANG"]);
        Assert.Equal("C.UTF-8", options.Environment["LC_ALL"]);
        Assert.Equal("1", options.Environment["DOTNET_NOLOGO"]);
        Assert.Equal("1", options.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"]);
    }

    [Fact]
    public void BatchCommandsKeepTheExistingSafeCmdQuotingVerbatim()
    {
        var launch = new AgentLaunch(@"C:\agent path\copilot.cmd", ["a & b", "%PATH%", "\"quoted\""], @"C:\project", @"C:\shim");
        var line = BatchCommandLine.For(launch.Command, launch.Args);
        var info = new ProcessStartInfo(@"C:\Windows\System32\cmd.exe", line);
        info.Environment.Remove("PATH");
        var options = ProcessRunner.TerminalOptions(info, launch, new TerminalSize(80, 24), false, null);
        Assert.Equal(info.FileName, options.App);
        Assert.Equal(line, Assert.Single(options.CommandLine));
        Assert.True(options.VerbatimCommandLine);
        Assert.False(options.UseAsyncIo);
        Assert.Equal(launch.ShimDirectory, options.Environment["PATH"]);
        Assert.False(options.Environment.ContainsKey("TERM"));
        Assert.False(options.Environment.ContainsKey("LC_ALL"));
    }

    [Theory]
    [InlineData(@"C:\tool\conpty.dll", @"\\?\C:\tool\conpty.dll")]
    [InlineData(@"\\server\share\conpty.dll", @"\\?\UNC\server\share\conpty.dll")]
    [InlineData(@"\\?\C:\tool\conpty.dll", @"\\?\C:\tool\conpty.dll")]
    public void ExtendedWindowsPathsPreserveLocalAndNetworkRoots(string input, string expected) =>
        Assert.Equal(expected, ProcessRunner.ExtendedWindowsPath(input));

    [Fact]
    public async Task AlreadyExitedChildrenCannotLoseTheirExitCodeDuringSubscription()
    {
        using var connection = new Connection { Finished = true };
        using var terminal = new PtyTerminal(connection, () => { });
        Assert.Equal(17, await terminal.Exited);
        Assert.True(connection.SubscribedBeforeCheckingExit);
    }

    [Fact]
    public void TerminalStreamsAndDimensionsPassThroughAndStopOwnsTheWholeTree()
    {
        using var connection = new Connection();
        var stops = 0;
        var terminal = new PtyTerminal(connection, () => stops++);
        Assert.Equal(123, terminal.Pid);
        Assert.Same(connection.ReaderStream, terminal.Output);
        Assert.Same(connection.WriterStream, terminal.Input);
        Assert.False(terminal.Exited.IsCompleted);
        terminal.Resize(new TerminalSize(90, 30));
        Assert.Equal((90, 30), connection.Size);
        terminal.Stop();
        terminal.Stop();
        terminal.Dispose();
        terminal.Dispose();
        Assert.Equal(1, stops);
        Assert.Equal(1, connection.Disposals);
        Assert.Equal(0, connection.Subscribers);
        Assert.Equal(0, connection.DirectKills);
    }

    [Fact]
    public void AStopFailureStillDisposesTheConnectionAndRemovesTheExitHandler()
    {
        using var connection = new Connection();
        var terminal = new PtyTerminal(connection, () => throw new IOException("stop failed"));
        Assert.Throws<IOException>(() => terminal.Dispose());
        Assert.Equal(1, connection.Disposals);
        Assert.Equal(0, connection.Subscribers);
        terminal.Dispose();
    }

    private sealed class Connection : IPtyConnection
    {
        public event EventHandler<PtyExitedEventArgs>? ProcessExited
        {
            add => Subscribers++;
            remove => Subscribers--;
        }
        public Stream ReaderStream { get; } = new MemoryStream();
        public Stream WriterStream { get; } = new MemoryStream();
        public int Pid => 123;
        public int ExitCode => 17;
        public bool Finished { get; init; }
        public int Subscribers { get; private set; }
        public bool SubscribedBeforeCheckingExit { get; private set; }
        public int Disposals { get; private set; }
        public int DirectKills { get; private set; }
        public (int Columns, int Rows) Size { get; private set; }
        public bool WaitForExit(int milliseconds) { SubscribedBeforeCheckingExit = Subscribers == 1; return Finished; }
        public void Kill() => DirectKills++;
        public void Resize(int cols, int rows) => Size = (cols, rows);
        public void Dispose() { Disposals++; ReaderStream.Dispose(); WriterStream.Dispose(); }
    }
}
