using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Milligram.Application;

namespace Milligram.Adapters.Companion;

public sealed record AgentHostDiscovery(int Pid, string Endpoint, int Protocol, string Version, DateTimeOffset Started, string? Instance = null);

/// <summary>Host files are local to a project; the pipe name remains short ASCII for Unix socket limits.</summary>
public sealed class AgentHostFiles(ProjectPaths paths)
{
    public string Directory => paths.RunDirectory;
    public string LockFile => Path.Combine(Directory, "agent-host.lock");
    public string DiscoveryFile => Path.Combine(Directory, "agent-host.json");
    public string LogFile => Path.Combine(Directory, "agent-host.log");
    public string Endpoint => EndpointFor(paths.Root, OperatingSystem.IsWindows());

    internal static string EndpointFor(string root, bool windows)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (windows) normalized = normalized.ToUpperInvariant();
        return "milligram-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..20].ToLowerInvariant();
    }
}

/// <summary>Holds project ownership until discovery is removed; a stale discovery file never grants ownership.</summary>
public sealed class AgentHostLease : IDisposable
{
    private readonly AgentHostFiles files;
    private readonly FileStream ownership;
    private AgentHostDiscovery? published;
    private bool disposed;

    public AgentHostLease(AgentHostFiles files)
    {
        this.files = files;
        System.IO.Directory.CreateDirectory(files.Directory);
        ownership = new FileStream(files.LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public void Publish(AgentHostDiscovery discovery)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        JsonFile.Write(files.DiscoveryFile, discovery);
        published = discovery;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            if (published is not null && ReadDiscovery(files) == published) File.Delete(files.DiscoveryFile);
        }
        finally { ownership.Dispose(); }
    }

    public static AgentHostDiscovery? ReadDiscovery(AgentHostFiles files)
    {
        try { return JsonFile.Read<AgentHostDiscovery>(files.DiscoveryFile); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or JsonException) { return null; }
    }
}
