using System.Runtime.InteropServices;
using Porta.Pty;

namespace Milligram.Adapters.Processes;

public sealed partial class ProcessRunner
{
    private static readonly Lazy<nint> unixTerminal = new(() => NativeLibrary.Load("libporta_pty", typeof(PtyProvider).Assembly, null));

    /// <summary>Checks prerequisites without starting an agent; loaded native handles remain valid for this process.</summary>
    public static string? TerminalIssue()
    {
        if (TerminalPlatformIssue(RuntimeInformation.RuntimeIdentifier, Environment.OSVersion.Version) is { } platform) return platform;
        if (OperatingSystem.IsWindows() && !OnPath("pwsh")) return "PowerShell (pwsh) is not on PATH. Copilot CLI needs PowerShell 6 or later; install PowerShell 7.";
        try
        {
            _ = OperatingSystem.IsWindows() ? conpty.Value : unixTerminal.Value;
            return null;
        }
        catch (Exception error) when (error is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or FileLoadException)
        {
            return "The agent terminal's native library could not load. Reinstall Milligram: " + error.Message;
        }
    }

    internal static string? TerminalPlatformIssue(string rid, Version osVersion) => rid switch
    {
        "linux-x64" or "linux-arm64" or "osx-x64" or "osx-arm64" => null,
        "win-x64" or "win-arm64" when osVersion >= new Version(10, 0, 17763) => null,
        "win-x64" or "win-arm64" => "The agent terminal needs Windows 10 version 1809 or later.",
        _ => $"The Milligram agent host does not support {rid}. Use agent.host: \"tmux\" or run Copilot yourself.",
    };
}
