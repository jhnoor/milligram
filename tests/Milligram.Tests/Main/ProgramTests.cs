using System.IO.Pipes;
using Milligram.Adapters.Companion;
using Milligram.Application;
using Milligram.Domain.Model;

namespace Milligram.Tests.Main;

public class ProgramTests
{
    [Fact]
    public async Task SourceOnlyInitializationPersistsTheFallbackForSubsequentCommands()
    {
        using var project = new TempProject(("App.csproj", "broken XML"), ("Source.cs", "namespace App; public class Source {}"));
        Assert.Equal(0, await global::Milligram.Main.Program.Main(["init", "--source-only", "--project", project.Root]));
        var paths = new ProjectPaths(project.Root);
        Assert.Equal(Milligram.Domain.Policies.ScanMode.SourceOnly, JsonFile.Read<Milligram.Domain.Policies.Policy>(paths.PolicyFile)!.Scan.Mode);
        Assert.Equal(0, await global::Milligram.Main.Program.Main(["ir", "--project", project.Root]));
        Assert.Equal("App.Source", Assert.Single(JsonFile.Read<CodeModel>(paths.ModelFile)!.Types).Id);
    }

    [Fact]
    public async Task PlainIrKeepsTheSourceOnlyFallbackAvailableWithoutAProject()
    {
        using var project = new TempProject(("Source.cs", "namespace App; public class Source {}"));
        Assert.Equal(0, await global::Milligram.Main.Program.Main(["ir", "--project", project.Root]));
        var model = JsonFile.Read<CodeModel>(new ProjectPaths(project.Root).ModelFile)!;
        Assert.Equal("App.Source", Assert.Single(model.Types).Id);
    }

    [Fact]
    public void TheProjectIsTheNearestDirectoryWithALocalOrRootPolicy()
    {
        using var project = new TempProject(
            ("local/.milligram/milligram.json", "{}"), ("local/src/App/A.cs", ""),
            ("legacy/milligram.json", "{}"), ("legacy/src/B.cs", ""),
            ("nested/.milligram/milligram.json", "{}"), ("nested/inner/.milligram/milligram.json", "{}"), ("nested/inner/src/C.cs", ""),
            ("none/.milligram/model.json", "{}"), ("none/src/D.cs", ""));
        string Find(params string[] parts) => global::Milligram.Main.Program.FindRoot(null, Path.Combine([project.Root, .. parts]));

        Assert.Equal(Path.Combine(project.Root, "local"), Find("local", "src", "App"));
        Assert.Equal(Path.Combine(project.Root, "legacy"), Find("legacy", "src"));
        Assert.Equal(Path.Combine(project.Root, "nested", "inner"), Find("nested", "inner", "src"));
        Assert.Equal(Path.Combine(project.Root, "none", "src"), Find("none", "src"));
        Assert.Equal(Path.Combine(project.Root, "local"), global::Milligram.Main.Program.FindRoot(Path.Combine(project.Root, "local", "src", ".."), project.Root));
    }

    [Fact]
    public async Task ARootPolicyFromAnEarlierVersionIsCopiedOnceAndThenLeftAlone()
    {
        const string text = "{\n  // hand-written\n  \"prefix\": \"App\",\n}\n";
        using var project = new TempProject(("milligram.json", text), ("Source.cs", "namespace App.Core; public class Source {}"));
        var paths = new ProjectPaths(project.Root);

        Assert.Equal(0, await global::Milligram.Main.Program.Main(["ir", "--project", project.Root]));
        Assert.Equal(text, File.ReadAllText(paths.PolicyFile));
        Assert.Equal(text, File.ReadAllText(paths.RootPolicyFile));
        Assert.Equal("App", JsonFile.Read<CodeModel>(paths.ModelFile)!.Prefix);

        File.WriteAllText(paths.RootPolicyFile, "{ \"prefix\": \"Other\" }");
        Assert.Equal(0, await global::Milligram.Main.Program.Main(["ir", "--project", project.Root]));
        Assert.Equal(text, File.ReadAllText(paths.PolicyFile));
        Assert.Equal("App", JsonFile.Read<CodeModel>(paths.ModelFile)!.Prefix);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedIrReturnsAnErrorAndPreservesTheModel(bool malformedPolicy)
    {
        using var project = new TempProject(("Source.cs", "namespace App; public class Source {}"));
        var paths = new ProjectPaths(project.Root);
        JsonFile.Write(paths.ModelFile, CodeModel.Empty("Previous", ""));
        if (malformedPolicy) project.Write(".milligram/milligram.json", "{ broken");
        var args = new List<string> { "ir", "--project", project.Root };
        if (!malformedPolicy) args.AddRange(["--msbuild", "Missing.csproj"]);

        Assert.Equal(1, await global::Milligram.Main.Program.Main(args.ToArray()));
        Assert.Equal("Previous", JsonFile.Read<CodeModel>(paths.ModelFile)!.Title);
    }

    [WindowsFact]
    public async Task StopWithMalformedPolicySendsStopToTheExistingNativeHost()
    {
        using var project = new TempProject((".milligram/milligram.json", "{ broken"));
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
        using var project = new TempProject((".milligram/milligram.json", "{ broken"));
        Assert.Equal(0, await global::Milligram.Main.Program.Main(["agent", "stop", "--project", project.Root]));
        Assert.Equal("{ broken", File.ReadAllText(new ProjectPaths(project.Root).PolicyFile));
    }

    [Theory]
    [InlineData("start")]
    [InlineData("attach")]
    [InlineData("status")]
    public async Task OtherAgentCommandsStillRejectMalformedPolicy(string command)
    {
        using var project = new TempProject((".milligram/milligram.json", "{ broken"));
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
