using System.Runtime.InteropServices;
using Milligram.Adapters.Processes;

namespace Milligram.Tests.Adapters;

public class ProgramPathTests
{
    /// <summary>A file system with the given files, compared the way Windows compares names.</summary>
    private static Func<string, bool> Files(params string[] files) => new HashSet<string>(files, StringComparer.OrdinalIgnoreCase).Contains;

    private static string? OnWindows(string command, string path, Func<string, bool> exists, string? pathExt = ProgramPath.DefaultPathExt) =>
        ProgramPath.Resolve(command, path, pathExt, windows: true, exists);

    private static string? OnUnix(string command, string path, Func<string, bool> exists) =>
        ProgramPath.Resolve(command, path, null, windows: false, exists);

    [Fact]
    public void OnWindowsEachDirectoryIsSearchedInOrderWithEachExtensionOfPathExt()
    {
        var files = Files(@"C:\winget\copilot.exe", @"C:\npm\copilot", @"C:\npm\copilot.cmd", @"C:\npm\copilot.ps1");

        Assert.Equal(@"C:\winget\copilot.EXE", OnWindows("copilot", @"C:\winget;C:\npm", files));
        Assert.Equal(@"C:\npm\copilot.CMD", OnWindows("copilot", @"C:\npm;C:\winget", files));
    }

    [Fact]
    public void OnWindowsPathExtOrderDecidesWithinADirectory()
    {
        var files = Files(@"C:\bin\tool.cmd", @"C:\bin\tool.exe");

        Assert.Equal(@"C:\bin\tool.EXE", OnWindows("tool", @"C:\bin", files));
        Assert.Equal(@"C:\bin\tool.cmd", OnWindows("tool", @"C:\bin", files, pathExt: ".cmd;.exe"));
    }

    [Fact]
    public void OnWindowsANameWithAnExtensionIsTriedAsGivenFirst()
    {
        var files = Files(@"C:\VS Code\bin\code", @"C:\VS Code\bin\code.cmd", @"C:\VS Code\bin\code.cmd.exe");

        Assert.Equal(@"C:\VS Code\bin\code.cmd", OnWindows("code.cmd", @"C:\VS Code\bin", files));
        Assert.Equal(@"C:\VS Code\bin\code.CMD", OnWindows("code", @"C:\VS Code\bin", files));
    }

    [Fact]
    public void OnWindowsAFileWithoutAnExtensionIsNeverTheProgram()
    {
        Assert.Null(OnWindows("copilot", @"C:\npm", Files(@"C:\npm\copilot")));
        Assert.Null(OnWindows("copilot", @"C:\npm.old", Files(@"C:\npm.old\copilot")));
        Assert.Null(OnWindows(".copilot", @"C:\npm", Files(@"C:\npm\.copilot")));
        Assert.Null(OnWindows("tmux", @"C:\msys\usr\bin", Files(@"C:\msys\usr\bin\tmux.sh")));
    }

    [Fact]
    public void OnWindowsPathEntriesMayBeQuotedEmptyPaddedOrEndInABackslash()
    {
        var files = Files(@"C:\Program Files\Git\cmd\git.exe", @"\git.exe");

        Assert.Equal(@"C:\Program Files\Git\cmd\git.EXE", OnWindows("git", @";"""";""C:\Program Files\Git\cmd"";", files));
        Assert.Equal(@"C:\Program Files\Git\cmd\git.EXE", OnWindows("git", @"C:\Program Files\Git\cmd\", files));
        Assert.Equal(@"C:\Program Files\Git\cmd\git.EXE", OnWindows("git", @"C:\x ; C:\Program Files\Git\cmd ", files));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void OnWindowsAMissingPathExtMeansTheDefault(string? pathExt) =>
        Assert.Equal(@"C:\bin\tool.CMD", OnWindows("tool", @"C:\bin", Files(@"C:\bin\tool.cmd"), pathExt));

    [Fact]
    public void OnWindowsPathExtEntriesMayLackTheirDotOrBePadded()
    {
        Assert.Equal(@"C:\bin\tool.exe", OnWindows("tool", @"C:\bin", Files(@"C:\bin\tool.exe"), pathExt: "exe"));
        Assert.Equal(@"C:\bin\tool.exe", OnWindows("tool", @"C:\bin", Files(@"C:\bin\tool.exe"), pathExt: " .cmd ; .exe "));
    }

    [Fact]
    public void ACommandWithADirectoryIsNotSearchedForOnPath()
    {
        var files = Files(@"C:\tools\x.exe", @"C:\bin\x.exe", @"\tools\x.exe", "C:x.exe", "/opt/x", "/usr/bin/x");

        Assert.Equal(@"C:\tools\x.EXE", OnWindows(@"C:\tools\x", @"C:\bin", files));
        Assert.Equal(@"\tools\x.EXE", OnWindows(@"\tools\x", @"C:\bin", files));
        Assert.Equal("C:x.exe", OnWindows("C:x.exe", @"C:\bin", files));
        Assert.Equal("C:/tools/x.exe", OnWindows("C:/tools/x.exe", @"C:\bin", Files("C:/tools/x.exe")));
        Assert.Null(OnWindows(@"C:\nowhere\x", @"C:\bin", files));
        Assert.Equal("/opt/x", OnUnix("/opt/x", "/usr/bin", files));
        Assert.Null(OnUnix("/nowhere/x", "/usr/bin", files));
    }

    [Fact]
    public void OnUnixTheNameIsTriedAsGivenInEachDirectoryInOrder()
    {
        var files = Files("/usr/bin/tmux", "/opt/homebrew/bin/tmux", "/usr/bin/copilot.exe", @"/usr/bin/a\b", "/opt/x");

        Assert.Equal("/opt/homebrew/bin/tmux", OnUnix("tmux", "/opt/homebrew/bin:/usr/bin", files));
        Assert.Equal("/usr/bin/tmux", OnUnix("tmux", "/usr/bin/:/opt/homebrew/bin", files));
        Assert.Null(OnUnix("copilot", "/usr/bin", files));
        Assert.Equal(@"/usr/bin/a\b", OnUnix(@"a\b", "/usr/bin", files));
        Assert.Null(OnUnix("x", "\"/opt\"", files));
    }

    [Fact]
    public void AMissingProgramIsNull()
    {
        Assert.Null(OnWindows("copilot", @"C:\bin;C:\npm", Files()));
        Assert.Null(OnUnix("tmux", "/usr/bin", Files()));
        Assert.Null(OnUnix("tmux", "", Files("tmux")));
        Assert.Null(ProgramPath.Resolve("tmux", null, null, windows: false, Files("tmux")));
        Assert.Null(OnUnix("", "/usr/bin", Files("/usr/bin/")));
    }

    [Theory]
    [InlineData(@"C:\VS Code\bin\code.cmd", true)]
    [InlineData(@"C:\x\run.BAT", true)]
    [InlineData(@"C:\x\copilot.exe", false)]
    [InlineData(@"C:\x\cmd", false)]
    public void BatchFilesAreRecognisedWhateverTheirCase(string program, bool batch) =>
        Assert.Equal(batch, ProgramPath.IsBatchFile(program));
}

public class BatchCommandLineTests
{
    private const string Code = @"C:\Users\me\AppData\Local\Programs\Microsoft VS Code\bin\code.cmd";
    private const string Start = $"/d /e:ON /v:OFF /s /c \"\"{Code}\"";

    [Fact]
    public void TheScriptIsQuotedAndTheWholeCommandQuotedOnceMore() =>
        Assert.Equal(Start + " -g C:\\src\\A.cs:12\"", BatchCommandLine.For(Code, ["-g", @"C:\src\A.cs:12"]));

    [Fact]
    public void PercentSignsInTheScriptsPathAreNotExpanded() =>
        Assert.Equal("/d /e:ON /v:OFF /s /c \"\"C:\\100%%cd:~,%\\x.cmd\"\"", BatchCommandLine.For(@"C:\100%\x.cmd", []));

    [Fact]
    public void TheScriptIsQuotedEvenWhenItNeedsNoQuotes() =>
        Assert.Equal("/d /e:ON /v:OFF /s /c \"\"C:\\bin\\x.cmd\"\"", BatchCommandLine.For(@"C:\bin\x.cmd", []));

    [Theory]
    [InlineData("plain-name_1.2+x@y", "plain-name_1.2+x@y")]
    [InlineData("a b", "\"a b\"")]
    [InlineData("", "\"\"")]
    [InlineData("a&calc", "\"a&calc\"")]
    [InlineData("a|b<c>d(e)^f!g", "\"a|b<c>d(e)^f!g\"")]
    [InlineData("50%", "\"50%%cd:~,%\"")]
    [InlineData("%PATH%", "\"%%cd:~,%PATH%%cd:~,%\"")]
    [InlineData("a\"b", "\"a\"\"b\"")]
    [InlineData("a\\\"b", "\"a\\\\\"\"b\"")]
    [InlineData(@"C:\dir\", "\"C:\\dir\\\\\"")]
    [InlineData(@"C:\dir\file.cs", @"C:\dir\file.cs")]
    [InlineData("ø", "ø")]
    public void EachArgumentIsQuotedByCmdsRules(string argument, string quoted) =>
        Assert.Equal(Start + " " + quoted + "\"", BatchCommandLine.For(Code, [argument]));

    [Theory]
    [InlineData("a\nb")]
    [InlineData("\nb")]
    [InlineData("a\rb")]
    [InlineData("a\0b")]
    public void AnArgumentWithALineBreakIsRefused(string argument) =>
        Assert.Throws<ArgumentException>(() => BatchCommandLine.For(Code, [argument]));
}

public class ProcessRunnerTests
{
    [WindowsFact]
    public void ABatchFileReceivesHostileArgumentsIntactAndRunsNothingElse()
    {
        using var project = new TempProject((@"b&n 100%\echo args.cmd", "@echo off\r\n>\"%~dp0args.txt\" echo(%*\r\n"));
        var script = Path.Combine(project.Root, "b&n 100%", "echo args.cmd");
        var canary = Path.Combine(project.Root, "canary.txt");
        string[] args = ["-g", @"C:\a b\x&calc.cs:1", "100%", "%OS%", "!OS!", @"C:\dir\", "", "^&|<>()", $"&echo pwned>\"{canary}\""];

        var (exitCode, output) = ProcessRunner.Capture(script, args);
        var received = Argv(File.ReadAllLines(Path.Combine(project.Root, "b&n 100%", "args.txt"))[0]);
        // Programs split a quote inside a quoted argument differently, so this one is only checked for what it runs.
        var (quotedExitCode, _) = ProcessRunner.Capture(script, $"\"&echo pwned>{canary}");

        Assert.True(exitCode == 0, output);
        Assert.Equal(args, received);
        Assert.Equal(0, quotedExitCode);
        Assert.False(File.Exists(canary));
    }

    [WindowsFact]
    public void ANameWithoutAnExtensionStartsItsCmdFileNotTheScriptBesideIt()
    {
        using var project = new TempProject((@"bin\hello.cmd", "@echo hello %~1\r\n"), (@"bin\hello", "#!/bin/sh\necho wrong\n"));
        var hello = Path.Combine(project.Root, "bin", "hello");

        var (exitCode, output) = ProcessRunner.Capture(hello, "big world");

        Assert.Equal(hello + ".CMD", ProcessRunner.Find(hello));
        Assert.Equal(0, exitCode);
        Assert.Equal("hello big world", output.Trim());
    }

    /// <summary>Splits a command line the way most Windows programs do, VS Code included.</summary>
    private static string[] Argv(string arguments)
    {
        var argv = CommandLineToArgvW("program " + arguments, out var count);
        try
        {
            return Enumerable.Range(1, count - 1).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!).ToArray();
        }
        finally
        {
            LocalFree(argv);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
