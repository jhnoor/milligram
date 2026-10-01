using System.Security.Cryptography;
using System.Text;
using Milligram.Application;

namespace Milligram.Adapters.Files;

/// <summary>Allows first-scan reuse only while source and project inputs remain unchanged across initialization.</summary>
public sealed class InitializationInputs : IDisposable
{
    private static readonly HashSet<string> Skipped = ["bin", ".git", "node_modules", ".milligram", ".vs", ".idea"];
    private readonly ProjectPaths paths;
    private readonly FileSystemWatcher? watcher;
    private readonly byte[]? fingerprint;
    private int changed;
    private int disposed;

    public InitializationInputs(ProjectPaths paths)
    {
        this.paths = paths;
        try
        {
            watcher = new FileSystemWatcher(paths.Root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            watcher.Changed += (_, e) => Changed(e.FullPath);
            watcher.Created += (_, e) => Changed(e.FullPath, Directory.Exists(e.FullPath));
            watcher.Deleted += (_, e) => Changed(e.FullPath, directory: true);
            watcher.Renamed += (_, e) => { Changed(e.OldFullPath, Directory.Exists(e.FullPath)); Changed(e.FullPath, Directory.Exists(e.FullPath)); };
            watcher.Error += (_, _) => Interlocked.Exchange(ref changed, 1);
            watcher.EnableRaisingEvents = true;
            fingerprint = Fingerprint(paths);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            watcher?.Dispose();
            watcher = null;
            changed = 1;
        }
    }

    /// <summary>Content hashes also detect changes on filesystems where native watcher events are missing.</summary>
    public bool IsCurrent()
    {
        if (Volatile.Read(ref disposed) != 0 || Volatile.Read(ref changed) != 0 || fingerprint is null) return false;
        var current = Fingerprint(paths);
        return current is not null && fingerprint.AsSpan().SequenceEqual(current) && Volatile.Read(ref changed) == 0;
    }

    private void Changed(string path, bool directory = false)
    {
        var relative = paths.Relative(path);
        if (Ignored(relative) || relative == ".gitignore" || relative == "milligram.json" || relative.EndsWith(".tmp", StringComparison.Ordinal)) return;
        if (directory || IsInput(relative)) Interlocked.Exchange(ref changed, 1);
    }

    private static bool Ignored(string relative) => relative.Split('/').Any(Skipped.Contains);

    private static bool IsInput(string relative)
    {
        if (Ignored(relative)) return false;
        var name = relative.Split('/')[^1];
        if (relative.Split('/').Contains("obj", StringComparer.OrdinalIgnoreCase))
            return name.Equals("project.assets.json", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".GlobalUsings.g.cs", StringComparison.OrdinalIgnoreCase);
        return name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".props", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".targets", StringComparison.OrdinalIgnoreCase) || name.Equals("packages.config", StringComparison.OrdinalIgnoreCase);
    }

    internal static byte[]? Fingerprint(ProjectPaths paths)
    {
        try
        {
            var src = Path.Combine(paths.Root, "src");
            var hasSrc = Directory.Exists(src);
            // The scanner follows an explicitly selected source root, but the recursive fingerprint skips nested links.
            if (hasSrc && (File.GetAttributes(src) & FileAttributes.ReparsePoint) != 0) return null;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData([hasSrc ? (byte)1 : (byte)0]);
            foreach (var path in ChangePoller.Files(paths.Root, _ => true, recurse: true, Skipped)
                .Select(file => file.Key).Where(path => IsInput(paths.Relative(path))).Order(StringComparer.Ordinal))
            {
                hash.AppendData(Encoding.UTF8.GetBytes(paths.Relative(path) + "\0"));
                if (path.EndsWith(".GlobalUsings.g.cs", StringComparison.OrdinalIgnoreCase))
                    hash.AppendData(BitConverter.GetBytes(File.GetLastWriteTimeUtc(path).Ticks));
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                hash.AppendData(SHA256.HashData(file));
            }
            return hash.GetHashAndReset();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0) watcher?.Dispose();
    }
}
