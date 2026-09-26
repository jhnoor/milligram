using System.Text.Json.Nodes;
using Milligram.Adapters.Cli;
using Milligram.Adapters.Companion;
using Milligram.Adapters.Files;
using Milligram.Application;
using Milligram.Domain.Mail;

namespace Milligram.Main;

public static class Program
{
    public const int DefaultPort = 5170;

    public static async Task<int> Main(string[] args)
    {
        var line = CommandLine.Parse(args);
        if (line.Has("version")) { Console.WriteLine(Version); return 0; }
        if (line.Has("help") || line.Command == "help") { Console.WriteLine(Help); return 0; }
        try
        {
            var composition = new Composition(FindRoot(line.Value("project")), SelfCommand());
            return line.Command switch
            {
                "serve" => await ServeAsync(composition, line),
                "init" => Init(composition, line),
                "ir" => Ir(composition),
                "crap" => await CrapAsync(composition, line),
                "mutate" => await MutateAsync(composition, line),
                "doctor" => await DoctorAsync(composition),
                "mail" => Mail(composition, line),
                "tell" => Tell(composition, line),
                "agent" => await AgentAsync(composition, line),
                _ => Usage($"Unknown command '{line.Command}'."),
            };
        }
        catch (MilligramException e)
        {
            Console.Error.WriteLine($"milligram: {e.Message}");
            return 1;
        }
    }

    private static string Version => typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private static async Task<int> ServeAsync(Composition c, CommandLine line)
    {
        var initialization = c.Initializer.Initialize(force: false);
        if (initialization is not null)
        {
            Console.WriteLine("Wrote milligram.json from the namespaces found in the source.");
            foreach (var described in initialization.Describe()) Console.WriteLine("  " + described);
        }
        c.Workspace.Load();
        if (initialization is not null)
        {
            Console.WriteLine("Checking what the metrics and the agent need (`milligram doctor` runs this again):");
            foreach (var check in await c.Doctor.RunAsync(CancellationToken.None))
                foreach (var described in check.Describe()) Console.WriteLine("  " + described);
        }
        if (c.Workspace.PolicyError is { } error) Console.Error.WriteLine(error);

        var (app, url) = await c.WebServer().StartAsync(line.IntValue("port", DefaultPort), CancellationToken.None);
        JsonFile.Write(c.Paths.ServerFile, new { url, pid = Environment.ProcessId, started = DateTimeOffset.UtcNow });
        var limit = c.WatchLimits.For(c.Paths.Root);
        using var watcher = new ProjectWatcher(c.Paths, c.Workspace, c.Actions, c.Events, windowsDrive: limit is not null);
        c.Actions.Regenerate("Scan");

        Console.WriteLine($"Milligram {Version} — {c.Paths.Root}");
        Console.WriteLine($"  Viewer: {url}");
        if (limit is not null)
        {
            Console.WriteLine($"  Watching: {limit.Detail}, so Milligram {watcher.Workaround}.");
            Console.WriteLine($"    Tip: {limit.Fix}.");
        }
        var agent = await c.Agent.StartAsync(wanted: !line.Has("no-agent"), CancellationToken.None);
        foreach (var banner in agent.Banner) Console.WriteLine("  " + banner);
        if (!line.Has("no-browser") && !Desktop.OpenUrl(url)) Console.WriteLine("  (Open the viewer URL in your browser.)");
        Console.WriteLine("  Ctrl+C stops the viewer.");

        await app.WaitForShutdownAsync();
        if (agent.Started && !line.Has("keep-agent") && !c.Workspace.Policy.Agent.KeepOnExit) c.Companion.Stop();
        File.Delete(c.Paths.ServerFile);
        return 0;
    }

    private static int Init(Composition c, CommandLine line)
    {
        if (c.Initializer.Initialize(line.Has("force")) is not { } initialization)
        {
            Console.WriteLine("milligram.json already exists (use --force to replace it).");
            return 0;
        }
        Console.WriteLine($"Wrote {c.Paths.PolicyFile}");
        foreach (var described in initialization.Describe()) Console.WriteLine(described);
        return 0;
    }

    private static int Ir(Composition c)
    {
        c.Workspace.Load();
        var model = c.Workspace.Generate();
        Console.WriteLine($"{model.Types.Count} types, {model.Edges.Count} dependencies -> {c.Paths.Relative(c.Paths.ModelFile)}");
        return 0;
    }

    private static async Task<int> CrapAsync(Composition c, CommandLine line)
    {
        c.Workspace.Load();
        var reports = line.Values("coverage").Select(Path.GetFullPath).ToList();
        var result = await c.Crap.RunAsync(reports, Console.WriteLine, CancellationToken.None);
        return result.TestExitCode == 0 ? 0 : 2;
    }

    private static async Task<int> MutateAsync(Composition c, CommandLine line)
    {
        c.Workspace.Load();
        var files = line.Arguments.Select(Path.GetFullPath).ToList();
        var result = await c.Mutation.RunAsync(files, line.Has("all"), Console.WriteLine, CancellationToken.None);
        Console.WriteLine($"Mutated {result.Members} members in {result.Projects} project(s): {result.Mutants} mutants.");
        return 0;
    }

    private static async Task<int> DoctorAsync(Composition c)
    {
        c.Workspace.Load();
        Console.WriteLine($"Milligram doctor: {c.Paths.Root}");
        var checks = await c.Doctor.RunAsync(CancellationToken.None);
        foreach (var check in checks)
            foreach (var described in check.Describe()) Console.WriteLine("  " + described);
        return checks.Any(check => check.Status == CheckStatus.Failed) ? 1 : 0;
    }

    private static int Mail(Composition c, CommandLine line)
    {
        var messages = c.Workspace.ToAgent.Take(keep: line.Has("peek"));
        if (messages.Count == 0) Console.WriteLine("No mail.");
        foreach (var message in messages)
        {
            var json = new JsonObject { ["id"] = message.Id, ["op"] = message.Op, ["at"] = message.At.ToString("O") };
            foreach (var (key, value) in message.Data) json[key] = value?.DeepClone();
            Console.WriteLine(json.ToJsonString(MilligramJson.Compact));
        }
        return 0;
    }

    private static int Tell(Composition c, CommandLine line)
    {
        var op = line.Arguments.FirstOrDefault();
        var rest = string.Join(' ', line.Arguments.Skip(1));
        JsonObject? data = op switch
        {
            MailMessage.Ops.Display when rest.Length > 0 => new JsonObject { ["context"] = rest, ["focus"] = line.Value("focus") },
            MailMessage.Ops.Notify when rest.Length > 0 => new JsonObject { ["text"] = rest },
            MailMessage.Ops.Reload => [],
            _ => null,
        };
        if (op is null || data is null)
            return Usage("usage: milligram tell display <real|proposalId> [--focus <nodeId>] | notify <text> | reload");
        c.Workspace.ToViewer.Post(op, data);
        return 0;
    }

    private static async Task<int> AgentAsync(Composition c, CommandLine line)
    {
        c.Workspace.Load();
        switch (line.Arguments.FirstOrDefault() ?? "status")
        {
            case "start":
                await c.Companion.StartAsync(CancellationToken.None);
                Console.WriteLine(c.Companion.AttachCommand);
                return 0;
            case "stop":
                c.Companion.Stop();
                return 0;
            case "attach":
                if (!c.Companion.OpenTerminal()) Console.WriteLine(c.Companion.AttachCommand);
                return 0;
            default:
                Console.WriteLine(c.Companion.IsRunning() ? $"running: {c.Companion.AttachCommand}" : "not running");
                return 0;
        }
    }

    /// <summary>The nearest directory at or above the working directory holding milligram.json, else the working directory.</summary>
    private static string FindRoot(string? explicitRoot)
    {
        if (explicitRoot is not null) return Path.GetFullPath(explicitRoot);
        for (var dir = new DirectoryInfo(Environment.CurrentDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "milligram.json"))) return dir.FullName;
        return Environment.CurrentDirectory;
    }

    /// <summary>How to run this same build again, for the agent's `milligram` shim.</summary>
    private static IReadOnlyList<string> SelfCommand()
    {
        var process = Environment.ProcessPath ?? "dotnet";
        var assembly = typeof(Program).Assembly.Location;
        return Path.GetFileNameWithoutExtension(process) == "dotnet" ? [process, assembly] : [process];
    }

    private static int Usage(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine("Run `milligram help` for usage.");
        return 64;
    }

    private const string Help = """
        milligram — a live architecture viewer for C# codebases, with a companion agent.

        usage: milligram [command] [options]

          serve (default)         Start the viewer (and the companion agent) for this project.
              --port N            Preferred port (default 5170; the next free one is used).
              --no-agent          Do not start the companion agent.
              --no-browser        Do not open a browser.
              --keep-agent        Leave the agent's tmux session running on exit.
          init [--force]          Write milligram.json from the namespaces in the source.
          ir                      Scan the source and write .milligram/model.json.
          crap [--coverage F]     Run tests with coverage (or read Cobertura file F) and score CRAP.
          mutate [--all] [files]  Mutation-test changed members (Stryker.NET); --all for whole files.
          doctor                  Check what the metrics and the agent need; print the fix for anything missing.
          mail [--peek]           Print (and remove) mail for the agent.
          tell display <ctx> [--focus id] | notify <text> | reload
                                  Send mail to the viewer.
          agent [status|start|stop|attach]
                                  Manage the companion agent's tmux session.

        Global: --project DIR     Project root (default: nearest directory with milligram.json).
        """;
}
