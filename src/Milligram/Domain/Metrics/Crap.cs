using Milligram.Domain.Model;

namespace Milligram.Domain.Metrics;

/// <summary>CRAP = comp² × (1 − cov)³ + comp, per member, from cyclomatic complexity and line coverage.</summary>
public static class Crap
{
    public static double Score(int complexity, double coverage) =>
        complexity * complexity * Math.Pow(1 - coverage, 3) + complexity;

    public static CrapSnapshot Compute(CodeModel model, LineHits hits, DateTimeOffset now)
    {
        var entries = new Dictionary<string, CrapEntry>();
        foreach (var member in model.Types.SelectMany(t => t.Members))
        {
            if (member.Complexity is not { } complexity) continue;
            entries[member.Id] = Entry(member, complexity, hits.For(member.Span.File));
        }
        return new CrapSnapshot(now, entries);
    }

    /// <summary>A file missing from the report was never loaded by a test: its members are uncovered.</summary>
    private static CrapEntry Entry(MemberNode member, int complexity, IReadOnlyDictionary<int, int>? fileHits)
    {
        if (fileHits is null) return new CrapEntry(complexity, 0, Score(complexity, 0), 0, 0, member.Hash);
        var lines = fileHits.Where(h => member.Span.ContainsLine(h.Key)).ToList();
        if (lines.Count == 0) return new CrapEntry(complexity, null, null, 0, 0, member.Hash);
        var covered = lines.Count(h => h.Value > 0);
        var coverage = (double)covered / lines.Count;
        return new CrapEntry(complexity, coverage, Score(complexity, coverage), lines.Count, covered, member.Hash);
    }
}
