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

    public int IntValue(string option, int fallback) => int.TryParse(Value(option), out var value) ? value : fallback;

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
