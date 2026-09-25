namespace Milligram.Domain.Metrics;

/// <summary>CRAP inputs and result for one member at the time of the last coverage run.</summary>
public sealed record CrapEntry(int Complexity, double? Coverage, double? Crap, int Coverable, int Covered, string Hash);

public sealed record CrapSnapshot(DateTimeOffset GeneratedAt, IReadOnlyDictionary<string, CrapEntry> Members)
{
    public static readonly CrapSnapshot Empty = new(DateTimeOffset.MinValue, new Dictionary<string, CrapEntry>());

    public bool IsEmpty => Members.Count == 0;
}

/// <summary>Mutation results for one member. Killed excludes timeouts; both count as detected.</summary>
public sealed record MutationEntry(int Killed, int Timeout, int Survived, int Uncovered, string? Hash)
{
    public static MutationEntry None(string? hash) => new(0, 0, 0, 0, hash);

    public int Detected => Killed + Timeout;
    public int Sites => Killed + Timeout + Survived + Uncovered;

    public MutationEntry Add(MutantStatus status) => status switch
    {
        MutantStatus.Killed => this with { Killed = Killed + 1 },
        MutantStatus.Timeout => this with { Timeout = Timeout + 1 },
        MutantStatus.Survived => this with { Survived = Survived + 1 },
        MutantStatus.NoCoverage => this with { Uncovered = Uncovered + 1 },
        _ => this,
    };
}

public sealed record MutationSnapshot(
    DateTimeOffset GeneratedAt,
    IReadOnlyDictionary<string, MutationEntry> Members,
    IReadOnlyDictionary<string, DateTimeOffset> Files)
{
    public static readonly MutationSnapshot Empty =
        new(DateTimeOffset.MinValue, new Dictionary<string, MutationEntry>(), new Dictionary<string, DateTimeOffset>());

    public bool Tested(string file) => Files.ContainsKey(file);
}

public sealed record MetricsSet(CrapSnapshot Crap, MutationSnapshot Mutation)
{
    public static readonly MetricsSet Empty = new(CrapSnapshot.Empty, MutationSnapshot.Empty);
}

public enum MutantStatus { Killed, Survived, NoCoverage, Timeout, CompileError, RuntimeError, Ignored, Pending }

/// <summary>A mutant from a mutation report, located in a project-relative file.</summary>
public sealed record Mutant(string File, int Line, int Column, MutantStatus Status, string Mutator);

/// <summary>Per-file line hit counts from a coverage report, keyed by project-relative path.</summary>
public sealed record LineHits(IReadOnlyDictionary<string, IReadOnlyDictionary<int, int>> Files)
{
    public IReadOnlyDictionary<int, int>? For(string file) => Files.GetValueOrDefault(file);
}
