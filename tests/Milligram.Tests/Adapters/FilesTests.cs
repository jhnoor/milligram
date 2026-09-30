using Milligram.Adapters.Files;
using Milligram.Adapters.Processes;

namespace Milligram.Tests.Adapters;

public class DrvFsTests
{
    /// <summary>From WSL2 on Ubuntu 24.04, plus a WSL1-style drive, a Linux disk mounted inside C:, and a path with a space.</summary>
    private const string Mounts = """
        none /usr/lib/wsl/drivers 9p ro,nosuid,nodev,noatime,dirsync,aname=drivers;fmask=222;dmask=222,mmap,access=client,msize=65536,trans=fd,rfd=7,wfd=7 0 0
        /dev/sdc / ext4 rw,relatime,discard,errors=remount-ro,data=ordered 0 0
        C:\134 /mnt/c 9p rw,noatime,aname=drvfs;path=C:\;uid=1001;gid=1001;symlinkroot=/mnt/,cache=5,access=client,msize=65536,trans=fd,rfd=6,wfd=6 0 0
        D: /mnt/d drvfs rw,noatime,uid=1000,gid=1000 0 0
        /dev/sdd /mnt/c/linux ext4 rw,relatime 0 0
        E:\134 /mnt/my\040drive 9p rw,aname=drvfs;path=E:\ 0 0
        """;

    [Theory]
    [InlineData("/mnt/c", true)]
    [InlineData("/mnt/c/code/NB-Assistant", true)]
    [InlineData("/mnt/d/src", true)]
    [InlineData("/mnt/my drive/src", true)]
    [InlineData("/home/me/src", false)]
    [InlineData("/mnt/cache/src", false)]
    [InlineData("/mnt/c/linux/src", false)]
    [InlineData("/usr/lib/wsl/drivers/x", false)]
    public void AWindowsDriveIsTheDrvFsMountClosestAboveThePath(string path, bool windowsDrive) =>
        Assert.Equal(windowsDrive, DrvFs.Holds(Mounts, path));

    [Fact]
    public void WithoutMountsNothingIsAWindowsDrive() => Assert.False(DrvFs.Holds("", "/mnt/c/code"));

    [Fact]
    public void OnlyAWindowsDriveHasALimit() => Assert.Null(new DrvFs().For(Path.GetTempPath()));
}

public class ChangePollerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFailedWalkRetriesWithoutLosingThePreviousSnapshot(bool denied)
    {
        var walks = 0;
        var changed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var poller = new ChangePoller(() => Interlocked.Increment(ref walks) switch
        {
            1 => Files(("A.cs", 1)),
            2 => throw (denied ? new UnauthorizedAccessException() : new IOException()),
            _ => Files(("A.cs", 2)),
        }, path => changed.TrySetResult(path), TimeSpan.FromMilliseconds(10));

        Assert.Equal("A.cs", await changed.Task.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task SlowWalksThrottlePollingBeyondTheMinimumInterval()
    {
        using var poller = new ChangePoller(() =>
        {
            Thread.Sleep(30);
            return Files(("A.cs", 1));
        }, _ => { }, TimeSpan.FromMilliseconds(1));
        for (var i = 0; i < 200 && poller.Interval == TimeSpan.Zero; i++) await Task.Delay(10);

        Assert.True(poller.Interval >= TimeSpan.FromMilliseconds(200));
    }

    [UnixFact]
    public void PollingDoesNotRecurseIntoDirectoryLinks()
    {
        using var project = new TempProject(("src/One.cs", ""), ("other/Other.cs", ""));
        var src = Path.Combine(project.Root, "src");
        Directory.CreateSymbolicLink(Path.Combine(src, "cycle"), src);
        Directory.CreateSymbolicLink(Path.Combine(src, "external"), Path.Combine(project.Root, "other"));

        var files = ChangePoller.Files(src, name => name.EndsWith(".cs", StringComparison.Ordinal), recurse: true).ToList();

        Assert.Equal(Path.Combine(src, "One.cs"), Assert.Single(files).Key);
    }

    private static Dictionary<string, Stamp> Files(params (string Path, long Length)[] files) =>
        files.ToDictionary(f => f.Path, f => new Stamp(f.Length, new DateTime(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc)));

    [Fact]
    public void ChangesAreFilesAddedRemovedOrStampedDifferentlyInPathOrder()
    {
        var before = Files(("a.cs", 1), ("b.cs", 2), ("c.cs", 3));
        var after = Files(("a.cs", 1), ("c.cs", 4), ("d.cs", 5));
        after["a2.cs"] = new Stamp(1, DateTime.UnixEpoch);
        before["a2.cs"] = new Stamp(1, DateTime.UnixEpoch.AddSeconds(1));

        Assert.Equal(["a2.cs", "b.cs", "c.cs", "d.cs"], ChangePoller.Changes(before, after));
        Assert.Empty(ChangePoller.Changes(after, after));
    }

    [Fact]
    public void FilesAreWalkedWithoutEnteringSkippedDirectories()
    {
        using var project = new TempProject(("src/A.cs", "a"), ("src/Deep/B.cs", "bb"), ("src/obj/C.cs", "c"), ("src/D.txt", "d"));
        var src = Path.Combine(project.Root, "src");

        var found = ChangePoller.Files(src, name => name.EndsWith(".cs", StringComparison.Ordinal), recurse: true, new HashSet<string> { "obj" }).ToList();
        var top = ChangePoller.Files(src, _ => true, recurse: false).Select(f => Path.GetFileName(f.Key)).Order(StringComparer.Ordinal);

        Assert.Equal([Path.Combine(src, "A.cs"), Path.Combine(src, "Deep", "B.cs")], found.Select(f => f.Key).Order(StringComparer.Ordinal));
        Assert.Equal(2, found.Single(f => f.Key.EndsWith("B.cs", StringComparison.Ordinal)).Value.Length);
        Assert.Equal(["A.cs", "D.txt"], top);
        Assert.Empty(ChangePoller.Files(Path.Combine(project.Root, "missing"), _ => true, recurse: true));
    }

    [Fact]
    public async Task EveryRoundReportsWhatChangedSinceTheLast()
    {
        using var project = new TempProject(("src/A.cs", "a"), ("src/B.cs", "b"));
        var src = Path.Combine(project.Root, "src");
        var seen = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var poller = new ChangePoller(() => ChangePoller.Files(src, _ => true, recurse: true), seen.Enqueue, TimeSpan.FromMilliseconds(20));
        for (var i = 0; i < 200 && poller.Interval == TimeSpan.Zero; i++) await Task.Delay(10);

        File.WriteAllText(Path.Combine(src, "A.cs"), "a changed");
        File.Delete(Path.Combine(src, "B.cs"));
        File.WriteAllText(Path.Combine(src, "C.cs"), "c");

        var expected = new[] { "A.cs", "B.cs", "C.cs" }.Select(name => Path.Combine(src, name)).ToList();
        for (var i = 0; i < 200 && !expected.All(seen.Contains); i++) await Task.Delay(25);
        Assert.Equal(expected, seen.Distinct().Order(StringComparer.Ordinal));
        Assert.True(poller.Interval >= TimeSpan.FromMilliseconds(20));
    }
}

public class WindowsSideWatcherTests
{
    private const string WindowsRoot = @"C:\Users\me\src\Shop";
    private const string LinuxRoot = "/mnt/c/Users/me/src/Shop";

    [Theory]
    [InlineData(@"C:\Users\me\src\Shop\Domain\Order.cs", "/mnt/c/Users/me/src/Shop/Domain/Order.cs")]
    [InlineData(@"c:\users\ME\src\shop\milligram.json", "/mnt/c/Users/me/src/Shop/milligram.json")]
    [InlineData(@"C:\Users\me\src\Shop", LinuxRoot)]
    [InlineData(@"C:\Users\me\src\ShopTools\A.cs", null)]
    [InlineData(@"D:\Shop\A.cs", null)]
    public void WindowsPathsUnderTheProjectMapToItsLinuxPath(string windowsPath, string? linuxPath)
    {
        Assert.Equal(linuxPath, WindowsSideWatcher.ToLinux(windowsPath, WindowsRoot, LinuxRoot));
        Assert.Equal(linuxPath, WindowsSideWatcher.ToLinux(windowsPath, WindowsRoot + @"\", LinuxRoot + "/"));
    }

    [Fact]
    public void TheScriptQuotesTheRootForPowerShell() =>
        Assert.Contains(@"-ArgumentList 'C:\it''s here'", WindowsSideWatcher.Script(@"C:\it's here"));

    [Theory]
    [InlineData(@"C:\p\src\obj\B.cs", true)]
    [InlineData(@"C:\p\src\obj", false)]
    [InlineData(@"C:\p\src\obj\project.assets.json", false)]
    [InlineData(@"C:\p\src\obj\Debug\net10.0\App.GlobalUsings.g.cs", false)]
    [InlineData(@"C:\p\src\obj\Debug\project.assets.json", true)]
    [InlineData(@"C:\p\src\obj\Debug\App.AssemblyInfo.cs", true)]
    [InlineData(@"C:\p\bin\obj\project.assets.json", true)]
    [InlineData(@"C:\p\src\Bin\Debug\A.dll", true)]
    [InlineData(@"C:\p\.git", true)]
    [InlineData(@"C:\p\.milligram\run\agent.json", true)]
    [InlineData(@"C:\p\.milligram\run", true)]
    [InlineData(@"C:\p\src\A.cs", false)]
    [InlineData(@"C:\p\src", false)]
    [InlineData(@"C:\p\src\objects\A.cs", false)]
    [InlineData(@"C:\p\.milligram\metrics\crap.json", false)]
    [InlineData(@"C:\p\.milligram\mail\to-viewer\1.json", false)]
    [InlineData(@"C:\p\.milligram\runner.json", false)]
    public void UnscannedDirectoriesAreSkippedWithWhateverTheyHold(string path, bool skipped) =>
        Assert.Equal(skipped, System.Text.RegularExpressions.Regex.IsMatch(path, WindowsSideWatcher.Unscanned, System.Text.RegularExpressions.RegexOptions.IgnoreCase));

    [Fact]
    public void TheScriptUsesThatFilter() => Assert.Contains($"-notmatch '{WindowsSideWatcher.Unscanned}'", WindowsSideWatcher.Script(@"C:\p"));

    [WindowsFact]
    public void TheScriptReportsChangesOutsideUnscannedDirectoriesAndStopsWhenItsInputCloses()
    {
        using var project = new TempProject(("src/A.cs", "a"), ("src/obj/B.cs", "b"));
        var lines = new System.Collections.Concurrent.BlockingCollection<string>();
        var script = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(WindowsSideWatcher.Script(project.Root)));
        var watcher = ProcessRunner.Follow("powershell.exe", ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", script], lines.Add);
        Assert.NotNull(watcher);
        try
        {
            Assert.True(lines.TryTake(out var ready, TimeSpan.FromSeconds(30)), "the watcher never said it was ready");
            Assert.Equal(WindowsSideWatcher.Ready, ready);

            File.WriteAllText(Path.Combine(project.Root, "src", "obj", "B.cs"), "b changed");
            File.WriteAllText(Path.Combine(project.Root, "src", "obj", "project.assets.json"), "{}");
            File.WriteAllText(Path.Combine(project.Root, "src", "A.cs"), "a changed");

            // Changes to directories may be reported too, so read until A.cs, which comes after anything B.cs raised.
            var reported = new List<string>();
            while (!reported.Contains(Path.Combine(project.Root, "src", "A.cs")) ||
                !reported.Contains(Path.Combine(project.Root, "src", "obj", "project.assets.json")))
            {
                Assert.True(lines.TryTake(out var line, TimeSpan.FromSeconds(10)), $"A.cs was not reported; got {string.Join(", ", reported)}");
                reported.Add(line);
            }
            Assert.DoesNotContain(Path.Combine(project.Root, "src", "obj", "B.cs"), reported);
            Assert.True(watcher.Stop(), "the watcher kept running after its input closed");
        }
        finally
        {
            watcher.Dispose();
        }
    }
}
