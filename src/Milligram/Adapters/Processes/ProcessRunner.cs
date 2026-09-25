using System.Diagnostics;
using System.Text.RegularExpressions;
using Milligram.Application;

namespace Milligram.Adapters.Processes;

public sealed partial class ProcessRunner : IProcessRunner
{
    public async Task<int> RunAsync(string command, IReadOnlyList<string> args, string workingDirectory, Action<string> onLine, CancellationToken cancellation)
    {
        using var process = new Process { StartInfo = StartInfo(command, args, workingDirectory, redirect: true) };
        process.OutputDataReceived += (_, e) => Forward(e.Data, onLine);
        process.ErrorDataReceived += (_, e) => Forward(e.Data, onLine);
        if (!Start(process, command, onLine)) return 127;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(cancellation);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        process.WaitForExit();
        return process.ExitCode;
    }

    /// <summary>Runs a short command synchronously and returns its exit code and combined output.</summary>
    public static (int ExitCode, string Output) Capture(string command, params string[] args)
    {
        try
        {
            using var process = Process.Start(StartInfo(command, args, Environment.CurrentDirectory, redirect: true))!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(15000))
            {
                process.Kill(entireProcessTree: true);
                return (124, "timed out");
            }
            return (process.ExitCode, output.Result + error.Result);
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            return (127, e.Message);
        }
    }

    /// <summary>Starts a process that outlives this call (terminals, browsers, editors).</summary>
    public static bool Launch(string command, IEnumerable<string> args, string? workingDirectory = null)
    {
        try
        {
            var info = StartInfo(command, args.ToList(), workingDirectory ?? Environment.CurrentDirectory, redirect: false);
            using var process = Process.Start(info);
            return process is not null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public static bool OnPath(string command) =>
        Path.IsPathRooted(command)
            ? File.Exists(command)
            : (Environment.GetEnvironmentVariable("PATH") ?? "")
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Any(dir => File.Exists(Path.Combine(dir, command)));

    private static ProcessStartInfo StartInfo(string command, IReadOnlyList<string> args, string workingDirectory, bool redirect)
    {
        var info = new ProcessStartInfo(command)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = redirect,
            RedirectStandardError = redirect,
            RedirectStandardInput = false,
            UseShellExecute = false,
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        info.Environment["DOTNET_NOLOGO"] = "1";
        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        return info;
    }

    private static bool Start(Process process, string command, Action<string> onLine)
    {
        try
        {
            return process.Start();
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            onLine($"Could not start {command}: {e.Message}");
            return false;
        }
    }

    private static void Forward(string? line, Action<string> onLine)
    {
        if (line is null) return;
        var clean = Ansi().Replace(line, "").TrimEnd();
        var lastReturn = clean.LastIndexOf('\r');
        if (lastReturn >= 0) clean = clean[(lastReturn + 1)..];
        if (clean.Length > 0) onLine(clean);
    }

    [GeneratedRegex(@"\x1B\[[0-9;?]*[ -/]*[@-~]")]
    private static partial Regex Ansi();
}
