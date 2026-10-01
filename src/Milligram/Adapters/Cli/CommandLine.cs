using Milligram.Application;

namespace Milligram.Adapters.Cli;

/// <summary>Parsed arguments: a command, positional arguments, and --options (repeatable).</summary>
public sealed class CommandLine
{
    private static readonly HashSet<string> Flags =
        ["no-agent", "no-browser", "all", "peek", "force", "keep-agent", "help", "version", "json"];

    private readonly Dictionary<string, List<string>> options = new(StringComparer.Ordinal);

    private CommandLine(string command, List<string> arguments)
    {
        Command = command;
        Arguments = arguments;
    }

    public string Command { get; }
    public IReadOnlyList<string> Arguments { get; }
    public string? Subcommand => Arguments.FirstOrDefault();
    public string? HostInstance => Command == "agent" && Subcommand == "host" ? Value("instance") : null;
    public bool IsAgentCommand => Command == "agent" && Arguments.Count <= 1 &&
        (Subcommand ?? "status") is "status" or "start" or "stop" or "attach" or "host";

    public static CommandLine Parse(IReadOnlyList<string> args)
    {
        var positional = new List<string>();
        var parsed = new CommandLine("", positional);
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg is "-h") arg = "--help";
            if (arg.StartsWith("--", StringComparison.Ordinal) && arg.Length > 2)
            {
                var (name, value) = Split(arg[2..]);
                if (value is null && !Flags.Contains(name) && i + 1 < args.Count) value = args[++i];
                parsed.Add(name, value ?? "");
            }
            else positional.Add(arg);
        }
        var command = positional.Count > 0 ? positional[0] : "serve";
        var result = new CommandLine(command, positional.Skip(1).ToList());
        foreach (var (name, values) in parsed.options) result.options[name] = values;
        return result;
    }

    public bool Has(string option) => options.ContainsKey(option);

    public string? Value(string option) => options.TryGetValue(option, out var values) ? values[^1] : null;

    public IReadOnlyList<string> Values(string option) => options.TryGetValue(option, out var values) ? values : [];

    /// <summary>Project evaluation is explicit until startup and watcher invalidation support the same inputs.</summary>
    public IReadOnlyList<string> ScanProjects()
    {
        if (!Has("msbuild") && !Has("configuration")) return [];
        if (Command != "ir") throw new MilligramException("--msbuild and --configuration are supported only by `milligram ir`.");
        if (!Has("msbuild")) throw new MilligramException("--configuration requires --msbuild FILE.csproj.");
        if (Values("msbuild").Any(string.IsNullOrWhiteSpace) || Values("msbuild").Any(value => value.StartsWith("--", StringComparison.Ordinal)))
            throw new MilligramException("--msbuild requires a C# project file; repeat it to scan several entry projects.");
        if (Has("configuration") && (string.IsNullOrWhiteSpace(Value("configuration")) || Value("configuration")!.StartsWith("--", StringComparison.Ordinal)))
            throw new MilligramException("--configuration requires a configuration name, such as Debug or Release.");
        return Values("msbuild");
    }

    public int Port(int fallback)
    {
        var text = Value("port");
        var port = text is null ? fallback : int.TryParse(text, out var parsed) ? parsed : 0;
        if (port is < 1 or > 65535) throw new MilligramException("--port must be an integer from 1 to 65535.");
        return port;
    }

    private void Add(string name, string value)
    {
        if (!options.TryGetValue(name, out var values)) options[name] = values = [];
        values.Add(value);
    }

    private static (string Name, string? Value) Split(string option)
    {
        var equals = option.IndexOf('=');
        return equals < 0 ? (option, null) : (option[..equals], option[(equals + 1)..]);
    }
}
