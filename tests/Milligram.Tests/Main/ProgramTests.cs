using System.IO.Pipes;
using Milligram.Adapters.Companion;
using Milligram.Application;

namespace Milligram.Tests.Main;

public class ProgramTests
{
    [WindowsFact]
    public async Task StopWithMalformedPolicySendsStopToTheExistingNativeHost()
    {
        using var project = new TempProject(("milligram.json", "{ broken"));
        var files = new AgentHostFiles(new ProjectPaths(project.Root));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var stopped = false;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serving = Task.Run(Serve);
        try
        {
            await ready.Task.WaitAsync(deadline.Token);
            Assert.Equal(0, await global::Milligram.Main.Program.Main(["agent", "stop", "--project", project.Root]));
            Assert.True(stopped);
        }
        finally
        {
            await deadline.CancelAsync();
            try { await serving; }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        }

        async Task Serve()
        {
            while (true)
            {
                await using var pipe = new NamedPipeServerStream(files.Endpoint, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                ready.TrySetResult();
                await pipe.WaitForConnectionAsync(deadline.Token);
                Assert.Equal(HostFrameKind.Hello, (await HostProtocol.ReadAsync(pipe, deadline.Token))!.Kind);
                await HostProtocol.WriteAsync(pipe, HostProtocol.Json(HostFrameKind.Hello, new HostHello(HostProtocol.Version, "test")), deadline.Token);
                var frame = await HostProtocol.ReadAsync(pipe, deadline.Token);
                if (frame is null) continue;
                Assert.Equal(HostFrameKind.Stop, frame.Kind);
                stopped = true;
                return;
            }
        }
    }

    [WindowsFact]
    public async Task StopWithMalformedPolicySucceedsWhenNeitherBackendHasASession()
    {
        using var project = new TempProject(("milligram.json", "{ broken"));
        Assert.Equal(0, await global::Milligram.Main.Program.Main(["agent", "stop", "--project", project.Root]));
        Assert.Equal("{ broken", File.ReadAllText(Path.Combine(project.Root, "milligram.json")));
    }

    [Theory]
    [InlineData("start")]
    [InlineData("attach")]
    [InlineData("status")]
    public async Task OtherAgentCommandsStillRejectMalformedPolicy(string command)
    {
        using var project = new TempProject(("milligram.json", "{ broken"));
        Assert.Equal(1, await global::Milligram.Main.Program.Main(["agent", command, "--project", project.Root]));
    }

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
