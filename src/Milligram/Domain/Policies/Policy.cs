using Milligram.Domain.Model;

namespace Milligram.Domain.Policies;

/// <summary>
/// The hand-edited description of how to draw a project (milligram.json).
/// The namespace tree is the architecture; the policy only orders, ranks, and filters it.
/// </summary>
public sealed record Policy
{
    public static readonly IReadOnlyList<string> DefaultExclude = ["**/bin/**", "**/obj/**"];

    public string? Title { get; init; }
    public string Src { get; init; } = ".";
    public IReadOnlyList<string> Exclude { get; init; } = DefaultExclude;
    public string Prefix { get; init; } = "";
    public IReadOnlyList<string> Order { get; init; } = [];

    /// <summary>Groups of namespace paths, inner (higher-level) first. Same group = same rank.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Levels { get; init; } = [];

    /// <summary>Namespace prefixes of external libraries to draw as ovals.</summary>
    public IReadOnlyList<string> Foreign { get; init; } = [];

    public IReadOnlyList<string> Omit { get; init; } = [];
    public IReadOnlyList<EdgeRule> OmitEdges { get; init; } = [];
    public IReadOnlyList<EdgeRule> EdgeKinds { get; init; } = [];
    public IReadOnlyList<Proposal> Proposals { get; init; } = [];
    public Thresholds Thresholds { get; init; } = new();
    public TestSettings Tests { get; init; } = new();
    public AgentSettings Agent { get; init; } = new();

    /// <summary>Command used to open a file, with {file} and {line} placeholders.</summary>
    public string? Editor { get; init; }

    public Proposal? FindProposal(string id) => Proposals.FirstOrDefault(p => p.Id == id);
}

/// <summary>Matches edges whose ends are covered by <see cref="From"/> and <see cref="To"/> (namespace paths or type names).</summary>
public sealed record EdgeRule
{
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public EdgeKind? Kind { get; init; }

    public bool Matches(string fromName, string toName) =>
        NamePath.Covers(From, fromName) && NamePath.Covers(To, toName);
}

/// <summary>A named what-if grouping of existing namespaces. Not instantiated in code.</summary>
public sealed record Proposal
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";

    /// <summary>Named components, inner (level 0) first.</summary>
    public IReadOnlyList<ProposalGroup> Layers { get; init; } = [];

    public IReadOnlyList<string> Omit { get; init; } = [];
}

public sealed record ProposalGroup
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public IReadOnlyList<ProposalEntry> Namespaces { get; init; } = [];
}

/// <summary>Either a namespace path / type name, or a nested group.</summary>
public sealed record ProposalEntry
{
    public string? Namespace { get; init; }
    public ProposalGroup? Group { get; init; }

    public static ProposalEntry Of(string ns) => new() { Namespace = ns };
    public static ProposalEntry Of(ProposalGroup group) => new() { Group = group };
}

public sealed record Thresholds
{
    public double CrapGood { get; init; } = 5;
    public double CrapBad { get; init; } = 30;
    public double MutationGood { get; init; } = 0.9;
    public double MutationBad { get; init; } = 0.5;

    /// <summary>When true, missing metrics count as the worst grade instead of unknown.</summary>
    public bool MissingIsWorst { get; init; }
}

public sealed record TestSettings
{
    /// <summary>Test projects to run for coverage. Empty = every detected test project.</summary>
    public IReadOnlyList<string> Projects { get; init; } = [];

    /// <summary>Optional `dotnet test --filter` expression.</summary>
    public string? Filter { get; init; }
}

public sealed record AgentSettings
{
    public static readonly IReadOnlyList<string> DefaultAllowTools = ["shell(milligram:*)"];

    public bool Enabled { get; init; } = true;
    public string Command { get; init; } = "copilot";
    public IReadOnlyList<string> Args { get; init; } = [];
    public IReadOnlyList<string> AllowTools { get; init; } = DefaultAllowTools;
    public string? Model { get; init; }

    /// <summary>"auto", "none", or a command with a {session} placeholder that attaches a terminal.</summary>
    public string Terminal { get; init; } = "auto";

    public bool KeepOnExit { get; init; }
}
