using System.Reflection;
using System.Text.Json.Nodes;
using Milligram.Adapters.Cli;
using Milligram.Adapters.Companion;
using Milligram.Adapters.Files;
using Milligram.Application;
using Milligram.Domain.Mail;
using Milligram.Domain.Policies;
using Milligram.Domain.Views;

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
            var composition = new Composition(FindRoot(line.Value("project"), Environment.CurrentDirectory), SelfCommand(), Version, line.ScanOptions());
            if (composition.Initializer.AdoptRootPolicy())
                Console.Error.WriteLine("milligram: copied milligram.json to .milligram/milligram.json, which is the only copy Milligram reads and writes from now on. The file at the project root is no longer used.");
            return line.Command switch
            {
                "serve" => await ServeAsync(composition, line),
                "init" => Init(composition, line),
                "ir" => await IrAsync(composition),
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

    /// <summary>The package version, including prerelease labels, without the SDK's commit suffix.</summary>
    private static string Version =>
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    private static async Task<int> ServeAsync(Composition c, CommandLine line)
    {
        var preferredPort = line.Port(DefaultPort);
        c.Workspace.Boards.ClearDrafts();
        if (!File.Exists(c.Paths.PolicyFile)) Console.WriteLine("Checking source and project inputs before initialization.");
        using var inputs = File.Exists(c.Paths.PolicyFile) ? null : c.WatchInitialization();
        var initialization = c.Initializer.Initialize(force: false, Console.WriteLine);
        if (initialization is not null)
        {
            Console.WriteLine("Wrote .milligram/milligram.json from the namespaces found in the source.");
            foreach (var described in initialization.Describe()) Console.WriteLine("  " + described);
        }
        c.Workspace.Load();
        if (initialization is not null)
        {
            Console.WriteLine("Checking what the metrics and the agent need (`milligram doctor` runs this again):");
            foreach (var check in await c.Doctor.RunAsync(CancellationToken.None))
                foreach (var described in check.Describe()) Console.WriteLine("  " + described);
        }
        var (app, url) = await c.WebServer().StartAsync(preferredPort, CancellationToken.None);
        JsonFile.Write(c.Paths.ServerFile, new { url, pid = Environment.ProcessId, started = DateTimeOffset.UtcNow });
        var limit = c.WatchLimits.For(c.Paths.Root);
        using var watcher = new ProjectWatcher(c.Paths, c.Workspace, c.Actions, c.Events, windowsDrive: limit is not null);
        c.Workspace.ReloadPolicy();
        c.Workspace.ReloadMetrics();
        if (c.Workspace.PolicyError is { } error) Console.Error.WriteLine(error);
        var scan = c.Actions.Start(initialization, () => inputs?.IsCurrent() == true);

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

        await scan;
        inputs?.Dispose();
        initialization = null;
        foreach (var step in ScanSummary.Of(c.Workspace).Describe()) Console.WriteLine("  " + step);

        await app.WaitForShutdownAsync();
        if (!line.Has("keep-agent") && !c.Workspace.Policy.Agent.KeepOnExit) agent.Ownership?.Stop();
        File.Delete(c.Paths.ServerFile);
        return 0;
    }

    private static int Init(Composition c, CommandLine line)
    {
        if (c.Initializer.Initialize(line.Has("force"), Console.WriteLine) is not { } initialization)
        {
            Console.WriteLine(".milligram/milligram.json already exists (use --force to replace it).");
            return 0;
        }
        Console.WriteLine($"Wrote {c.Paths.PolicyFile}");
        foreach (var described in initialization.Describe()) Console.WriteLine(described);
        return 0;
    }

    private static async Task<int> IrAsync(Composition c)
    {
        c.Workspace.Load();
        if (c.Workspace.PolicyError is { } error) throw new MilligramException(error);
        await c.Jobs.Enqueue("Scan", (log, _) =>
        {
            c.Workspace.Generate(message => { log(message); Console.WriteLine(message); });
            return Task.FromResult("Scan complete.");
        });
        if (c.Jobs.Status.State == JobState.Failed) throw new MilligramException(c.Jobs.Status.Message ?? "Scan failed.");
        Console.WriteLine($"Wrote {c.Paths.Relative(c.Paths.ModelFile)}");
        foreach (var line in ScanSummary.Of(c.Workspace).Describe()) Console.WriteLine(line);
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
        if (op == "board") return TellBoard(c, rest.Length > 0 ? Path.GetFullPath(rest) : null);
        JsonObject? data = op switch
        {
            MailMessage.Ops.Display when rest.Length > 0 => new JsonObject { ["context"] = rest, ["focus"] = line.Value("focus") },
            MailMessage.Ops.Notify when rest.Length > 0 => new JsonObject { ["text"] = rest },
            MailMessage.Ops.Reload => [],
            _ => null,
        };
        if (op is null || data is null)
            return Usage(TellUsage);
        c.Workspace.ToViewer.Post(op, data);
        return 0;
    }

    private const string TellUsage =
        "usage: milligram tell display <real|proposalId|board:id> [--focus <nodeId>] | board [file] | notify <text> | reload";

    /// <summary>Draws the pen file (or another file) as a board and shows it, saying which code references didn't resolve.</summary>
    private static int TellBoard(Composition c, string? file)
    {
        var board = c.Workspace.Boards.Draw(file);
        c.Workspace.ReloadPolicy();
        c.Workspace.ReloadModel();
        var missing = BoardViewBuilder.Unresolved(board, c.Workspace.Model, c.Workspace.Policy.Prefix);
        c.Workspace.ToViewer.Post(MailMessage.Ops.Display, new JsonObject { ["context"] = BoardViewBuilder.ContextOf(board.Id!) });
        Console.WriteLine($"Drew board {board.Id}: {board.Boxes.Count} boxes, {board.Arrows.Count} arrows. Keep or discard it in the viewer.");
        if (missing.Count > 0)
            Console.WriteLine($"Not in the code, so drawn as missing: {string.Join(", ", missing)}. Use a type id such as Shop.Orders.OrderService, or Type.Member.");
        return 0;
    }

    private static async Task<int> AgentAsync(Composition c, CommandLine line)
    {
        if (!line.IsAgentCommand) return Usage("Use `milligram agent status|start|stop|attach` with at most one subcommand.");
        c.Workspace.ReloadPolicy();
        if (line.Subcommand == "stop" && c.Workspace.PolicyError is not null)
        {
            c.Companion.StopWithoutPolicy();
            return 0;
        }
        if (c.Workspace.PolicyError is { } error) throw new MilligramException(error);
        switch (line.Subcommand ?? "status")
        {
            case "host":
                return await c.AgentHost.RunAsync(CancellationToken.None, line.HostInstance);
            case "start":
                await c.Companion.StartAsync(CancellationToken.None);
                Console.WriteLine(c.Companion.AttachCommand);
                return 0;
            case "stop":
                c.Companion.Stop();
                return 0;
            case "attach":
                if (c.Companion.Host == AgentHostKind.Milligram)
                {
                    Console.WriteLine("Attached terminal: Ctrl+] then d detaches; Ctrl+] twice sends Ctrl+] to the agent.");
                    return await c.Attachment.RunAsync(CancellationToken.None);
                }
                if (!c.Companion.OpenTerminal()) Console.WriteLine(c.Companion.AttachCommand);
                return 0;
            default:
                Console.WriteLine(c.Companion.IsRunning() ? $"running: {c.Companion.AttachCommand}" : "not running");
                return 0;
        }
    }

    /// <summary>The nearest directory at or above the working directory with a policy (or a root one to adopt), else the working directory.</summary>
    internal static string FindRoot(string? explicitRoot, string workingDirectory)
    {
        if (explicitRoot is not null) return Path.GetFullPath(explicitRoot);
        for (var dir = new DirectoryInfo(workingDirectory); dir is not null; dir = dir.Parent)
        {
            var paths = new ProjectPaths(dir.FullName);
            if (File.Exists(paths.PolicyFile) || File.Exists(paths.RootPolicyFile)) return dir.FullName;
        }
        return workingDirectory;
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
                                  On exit, stop only the agent session this viewer started.
              --port N            Preferred port, 1-65535 (default 5170; tries up to 29 following ports).
              --no-agent          Do not start the companion agent.
              --no-browser        Do not open a browser.
              --keep-agent        Leave the companion session running on exit.
          init [--force]          Write .milligram/milligram.json from the namespaces in the source.
          ir                      Scan the source and write .milligram/model.json.
          crap [--coverage F]     Run tests with coverage (or read Cobertura file F) and score CRAP.
          mutate [--all] [files]  Mutation-test changed members (Stryker.NET); --all for whole files.
          doctor                  Check what the metrics and the agent need; print the fix for anything missing.
          mail [--peek]           Print (and remove) mail for the agent.
          tell display <ctx> [--focus id] | board [file] | notify <text> | reload
                                  Send mail to the viewer. `board` draws .milligram/board.json (or file).
                                  <ctx> is real, a proposal id, or board:<id>.
          agent [status|start|stop|attach]
                                  Manage the companion agent session.
                                  With an unreadable policy, stop checks both project backends.
                                  Native attach uses this terminal; Ctrl+] then d detaches.

        Global: --project DIR     Project root (default: nearest directory with .milligram/milligram.json).
        Scan options (serve, init, ir; saved when creating .milligram/milligram.json):
          --msbuild FILE          Select an entry C# project (repeatable). Replaces src selection; keeps excludes.
          --configuration NAME    Select a build configuration; otherwise use the project's default.
          --source-only           Use the approximate scanner without evaluating or executing project tasks.
                                  Without an override, use scan settings from .milligram/milligram.json (default: auto).
        """;
}
