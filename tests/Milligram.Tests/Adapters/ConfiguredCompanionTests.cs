using System.Text.Json;
using Milligram.Adapters.Companion;
using Milligram.Application;
using Milligram.Domain.Policies;

namespace Milligram.Tests.Adapters;

public class ConfiguredCompanionTests
{
    [Theory]
    [InlineData(AgentHostKind.Tmux)]
    [InlineData(AgentHostKind.Milligram)]
    public async Task SelectionWaitsForPolicyLoadingAndAllControlStaysOnThatBackend(AgentHostKind initial)
    {
        var policy = AgentHostKind.Tmux;
        var reads = 0;
        var tmux = new FakeCompanion();
        var native = new FakeCompanion();
        var companion = new ConfiguredCompanion(() => { reads++; return policy; }, tmux, native);
        Assert.Equal(0, reads);
        policy = initial;
        var selected = initial == AgentHostKind.Tmux ? tmux : native;
        var unused = initial == AgentHostKind.Tmux ? native : tmux;
        selected.TerminalOpens = true;
        selected.CreatedSession = false;
        unused.Available = false;
        Assert.Equal(initial, companion.Host);
        policy = initial == AgentHostKind.Tmux ? AgentHostKind.Milligram : AgentHostKind.Tmux;

        Assert.True(companion.IsAvailable(out var reason));
        Assert.Empty(reason);
        Assert.False(await companion.StartAsync(CancellationToken.None));
        Assert.True(companion.IsRunning());
        Assert.True(companion.OpenTerminal());
        Assert.Equal(selected.SessionName, companion.SessionName);
        Assert.Equal(selected.AttachCommand, companion.AttachCommand);
        companion.Ring();
        companion.Stop();
        Assert.False(companion.IsRunning());
        Assert.Equal(1, selected.Starts);
        Assert.Equal(1, selected.Rings);
        Assert.Equal(0, unused.Starts);
        Assert.Equal(0, unused.Rings);
        Assert.Equal(initial, companion.Host);
        Assert.Equal(1, reads);
    }

    [Fact]
    public void AnUnsupportedHostNeverFallsBackToControllingAnotherSession()
    {
        var tmux = new FakeCompanion();
        var native = new FakeCompanion();
        var companion = new ConfiguredCompanion(() => (AgentHostKind)99, tmux, native);
        var error = Assert.Throws<MilligramException>(() => companion.Stop());
        Assert.Contains("agent.host", error.Message);
        Assert.Equal(0, tmux.Starts + native.Starts);
    }

    [Theory]
    [InlineData("{}", AgentHostKind.Tmux)]
    [InlineData("{\"agent\":{\"host\":\"tmux\"}}", AgentHostKind.Tmux)]
    [InlineData("{\"agent\":{\"host\":\"milligram\"}}", AgentHostKind.Milligram)]
    public void HostSettingsUseTheSharedJsonConventionAndKeepTheExistingDefault(string json, AgentHostKind expected)
    {
        var policy = JsonSerializer.Deserialize<Policy>(json, MilligramJson.Options)!;
        Assert.Equal(expected, policy.Agent.Host);
        Assert.Contains($"\"host\": \"{expected.ToString().ToLowerInvariant()}\"", JsonSerializer.Serialize(policy, MilligramJson.Options));
    }

    [Fact]
    public void UnknownHostNamesAreConfigurationErrors() =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Policy>("""{"agent":{"host":"unknown"}}""", MilligramJson.Options));
}
