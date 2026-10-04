using Milligram.Domain.Metrics;
using Milligram.Domain.Model;

namespace Milligram.Application;

/// <summary>Null contexts denote an imported report without test-run provenance.</summary>
public sealed record CoverageSample(LineHits Hits, IReadOnlyList<CompilerContext>? Contexts);

/// <summary>Only a uniquely identified compiler context receives a fresh coverage score.</summary>
public static class CoverageAttribution
{
    public static CrapSnapshot Compute(CodeModel model, IReadOnlyList<CoverageSample> samples, DateTimeOffset now, Action<string> log)
    {
        var groups = model.Types.Where(type => type.Context is not null).GroupBy(type => type.Context!).ToList();
        var contexts = groups.Select(group => group.Key).ToList();
        var entries = Crap.Compute(model with { Types = model.Types.Where(type => type.Context is null).ToList() },
            LineHits.Combine(samples.Select(sample => sample.Hits)), now).Members.ToDictionary();
        foreach (var group in groups)
        {
            var context = group.Key;
            var measured = new List<LineHits>();
            if (context.Resolved)
                foreach (var sample in samples)
                {
                    var candidates = sample.Contexts ?? contexts;
                    if (!candidates.Contains(context)) continue;
                    if (sample.Hits.Modules.Count == 0)
                    {
                        if (candidates.Distinct().Count() == 1) measured.Add(sample.Hits);
                        continue;
                    }
                    if (candidates.Where(candidate => candidate.Assembly == context.Assembly).Distinct().Count() != 1) continue;
                    if (sample.Hits.Modules.TryGetValue(context.Assembly, out var module)) measured.Add(module);
                    else if (sample.Contexts is not null) measured.Add(new LineHits(new Dictionary<string, IReadOnlyDictionary<int, int>>()));
                }
            if (measured.Count == 0)
            {
                log($"Coverage unknown for {context.Label}: no report uniquely identifies this compiler context. Run `milligram crap` with a test project referencing it.");
                continue;
            }
            var subset = model with { Types = group.ToList() };
            foreach (var entry in Crap.Compute(subset, LineHits.Combine(measured), now).Members) entries.Add(entry.Key, entry.Value);
        }
        return new CrapSnapshot(now, entries);
    }
}
