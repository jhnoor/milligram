using Milligram.Adapters.Companion;
using Milligram.Application;

namespace Milligram.Tests.Adapters;

public class AgentHostLeaseTests
{
    [Fact]
    public void OnlyOneOwnerCanPublishForAProjectAtATime()
    {
        using var project = new TempProject();
        var files = new AgentHostFiles(new ProjectPaths(project.Root));
        var discovery = Discovery(files);
        using (var owner = new AgentHostLease(files))
        {
            owner.Publish(discovery);
            Assert.Equal(discovery, AgentHostLease.ReadDiscovery(files));
            Assert.Throws<IOException>(() => new AgentHostLease(files));
            Assert.Equal(discovery, AgentHostLease.ReadDiscovery(files));
        }
        Assert.Null(AgentHostLease.ReadDiscovery(files));
        Assert.True(File.Exists(files.LockFile));
        using var next = new AgentHostLease(files);
        next.Publish(discovery with { Pid = 456 });
        Assert.Equal(456, AgentHostLease.ReadDiscovery(files)!.Pid);
    }

    [Fact]
    public void AnUnownedStaleRecordDoesNotPreventAcquiringTheLock()
    {
        using var project = new TempProject();
        var files = new AgentHostFiles(new ProjectPaths(project.Root));
        JsonFile.Write(files.DiscoveryFile, Discovery(files));
        using (var owner = new AgentHostLease(files)) owner.Publish(Discovery(files) with { Pid = 456 });
        Assert.False(File.Exists(files.DiscoveryFile));
    }

    [Fact]
    public void AnOwnerDoesNotDeleteARecordItDidNotPublish()
    {
        using var project = new TempProject();
        var files = new AgentHostFiles(new ProjectPaths(project.Root));
        var replacement = Discovery(files) with { Started = DateTimeOffset.UnixEpoch.AddSeconds(1) };
        using (var owner = new AgentHostLease(files))
        {
            owner.Publish(Discovery(files));
            JsonFile.Write(files.DiscoveryFile, replacement);
        }
        Assert.Equal(replacement, AgentHostLease.ReadDiscovery(files));
        using (var unpublished = new AgentHostLease(files)) { }
        Assert.Equal(replacement, AgentHostLease.ReadDiscovery(files));
    }

    [Fact]
    public void InvalidDiscoveryCanBeReplacedAndDoesNotRetainOwnership()
    {
        using var project = new TempProject();
        var files = new AgentHostFiles(new ProjectPaths(project.Root));
        project.Write(".milligram/run/agent-host.json", "{");
        Assert.Null(AgentHostLease.ReadDiscovery(files));
        var owner = new AgentHostLease(files);
        owner.Publish(Discovery(files));
        owner.Dispose();
        owner.Dispose();
        Assert.Throws<ObjectDisposedException>(() => owner.Publish(Discovery(files)));
        using var next = new AgentHostLease(files);
    }

    [Fact]
    public void DisposingAnOldOwnerAgainCannotRemoveANewOwnersRecord()
    {
        using var project = new TempProject();
        var files = new AgentHostFiles(new ProjectPaths(project.Root));
        var discovery = Discovery(files);
        var previous = new AgentHostLease(files);
        previous.Publish(discovery);
        previous.Dispose();
        using var next = new AgentHostLease(files);
        next.Publish(discovery);
        previous.Dispose();
        Assert.Equal(discovery, AgentHostLease.ReadDiscovery(files));
    }

    [Fact]
    public void EndpointNamesAreShortAsciiAndNormalizeWindowsCaseAndTrailingSeparators()
    {
        using var project = new TempProject();
        var root = Path.Combine(project.Root, "Agent with 漢字 and spaces");
        var windows = AgentHostFiles.EndpointFor(root, windows: true);
        Assert.Matches("^milligram-[a-f0-9]{20}$", windows);
        Assert.Equal(windows, AgentHostFiles.EndpointFor(root.ToUpperInvariant() + Path.DirectorySeparatorChar, windows: true));
        Assert.NotEqual(AgentHostFiles.EndpointFor(root, windows: false), AgentHostFiles.EndpointFor(root.ToUpperInvariant(), windows: false));
        Assert.NotEqual(windows, AgentHostFiles.EndpointFor(root + "2", windows: true));
    }

    private static AgentHostDiscovery Discovery(AgentHostFiles files) => new(123, files.Endpoint, HostProtocol.Version, "0.2.0", DateTimeOffset.UnixEpoch);
}
