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
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The full path of a program on PATH, trying each extension in PATHEXT on Windows; null when there is none.</summary>
    public static string? Find(string command) =>
        ProgramPath.Resolve(command, Environment.GetEnvironmentVariable("PATH"), Environment.GetEnvironmentVariable("PATHEXT"),
            OperatingSystem.IsWindows(), File.Exists);

    public static bool OnPath(string command) => Find(command) is not null;

    /// <summary>Opens a URL or a document with the program the user chose for it (ShellExecute, on Windows).</summary>
    public static bool Open(string target)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return true;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Starts the program that <see cref="Find"/> resolves, so lookup and launch agree. A .cmd or .bat file runs through
    /// cmd.exe with arguments quoted by cmd.exe's rules, which .NET's escaping doesn't cover.
    /// </summary>
    private static ProcessStartInfo StartInfo(string command, IReadOnlyList<string> args, string workingDirectory, bool redirect)
    {
        var program = Find(command) ?? command;
        var info = OperatingSystem.IsWindows() && ProgramPath.IsBatchFile(program)
            ? new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), BatchCommandLine.For(program, args))
            : new ProcessStartInfo(program, args);
        info.WorkingDirectory = workingDirectory;
        info.RedirectStandardOutput = redirect;
        info.RedirectStandardError = redirect;
        info.RedirectStandardInput = false;
        info.UseShellExecute = false;
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
