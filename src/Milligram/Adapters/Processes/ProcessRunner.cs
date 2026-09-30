using System.Diagnostics;
using System.Text.RegularExpressions;
using Milligram.Application;

namespace Milligram.Adapters.Processes;

public sealed partial class ProcessRunner : IProcessRunner
{
    private const string UndrainedOutput = "Stopped waiting for redirected output after the process exited; a child may still have the pipe open.";

    public async Task<int> RunAsync(string command, IReadOnlyList<string> args, string workingDirectory, Action<string> onLine, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = StartInfo(command, args, workingDirectory, redirect: true) };
        if (!Start(process, command, onLine)) return 127;
        int code;
        bool drained;
        await using (var output = new ProcessOutput(process.StandardOutput, process.StandardError,
            line => Forward(line, onLine), line => Forward(line, onLine), lines: true))
            (code, drained) = await CompleteAsync(process, output, cancellation);
        if (!drained) onLine(UndrainedOutput);
        return code;
    }

    /// <summary>Runs a short command synchronously and returns its exit code and combined output.</summary>
    public static (int ExitCode, string Output) Capture(string command, params string[] args) =>
        CaptureAsync(command, args).GetAwaiter().GetResult();

    private static async Task<(int ExitCode, string Output)> CaptureAsync(string command, string[] args)
    {
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var process = Process.Start(StartInfo(command, args, Environment.CurrentDirectory, redirect: true))!;
            var stdout = new System.Text.StringBuilder();
            var stderr = new System.Text.StringBuilder();
            int code;
            bool drained;
            var output = new ProcessOutput(process.StandardOutput, process.StandardError,
                text => stdout.Append(text), text => stderr.Append(text), lines: false);
            await using (output.ConfigureAwait(false))
                (code, drained) = await CompleteAsync(process, output, deadline.Token).ConfigureAwait(false);
            return (code, stdout.ToString() + stderr + (drained ? "" : Environment.NewLine + UndrainedOutput));
        }
        catch (OperationCanceledException) { return (124, "timed out"); }
        catch (System.ComponentModel.Win32Exception e)
        {
            return (127, e.Message);
        }
    }

    /// <summary>Observe native exit separately from EOF, then give inherited pipes a bounded drain.</summary>
    private static async Task<(int ExitCode, bool Drained)> CompleteAsync(Process process, ProcessOutput output, CancellationToken cancellation)
    {
        try
        {
            var exited = process.WaitForExitAsync(cancellation);
            if (await Task.WhenAny(exited, output.Completion).ConfigureAwait(false) == output.Completion)
                await output.Completion.ConfigureAwait(false);
            await exited.ConfigureAwait(false);
            var drained = await output.DrainAsync(TimeSpan.FromSeconds(2), cancellation).ConfigureAwait(false);
            return (process.ExitCode, drained);
        }
        catch
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                process.WaitForExit(2000);
            }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception && process.HasExited) { }
            throw;
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

    /// <summary>
    /// Starts a process that runs alongside this one and hands over each line it prints, in UTF-8. Stopping it closes
    /// the process's input, which tells a well-behaved child to finish, and kills it if it hasn't within two seconds.
    /// </summary>
    public static Followed? Follow(string command, IReadOnlyList<string> args, Action<string> onLine)
    {
        var info = StartInfo(command, args, Environment.CurrentDirectory, redirect: true);
        info.RedirectStandardInput = true;
        info.StandardOutputEncoding = System.Text.Encoding.UTF8;
        var process = new Process { StartInfo = info };
        process.OutputDataReceived += (_, e) => { if (e.Data is { } line) onLine(line); };
        process.ErrorDataReceived += (_, _) => { };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            process.Dispose();
            return null;
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return new Followed(process);
    }

    public sealed class Followed(Process process) : IDisposable
    {
        /// <summary>True when the process finished on its own once its input closed.</summary>
        public bool Stop()
        {
            try
            {
                process.StandardInput.Close();
                if (process.WaitForExit(2000)) return true;
                process.Kill(entireProcessTree: true);
            }
            catch (Exception e) when (e is InvalidOperationException or IOException) { }
            return false;
        }

        public void Dispose()
        {
            Stop();
            process.Dispose();
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
