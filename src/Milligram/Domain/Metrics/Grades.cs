using Milligram.Domain.Model;
using Milligram.Domain.Policies;

namespace Milligram.Domain.Metrics;

public sealed record CrapSummary(double Mu, double Max, double Sigma, int Count, bool Stale)
{
    /// <summary>μ + σ: the number graded, so a few terrible members show even when the mean is fine.</summary>
    public double Score => Mu + Sigma;
}

public sealed record MutationSummary(int Killed, int Survived, int Uncovered, int Sites, bool Stale)
{
    public double? Score => Sites == 0 ? null : (double)Killed / Sites;
}

/// <summary>1 (worst) to 10 (best), or null when unknown.</summary>
public sealed record Grades(int? Crap, int? Mutation)
{
    public static readonly Grades Unknown = new(null, null);
}

public static class Grading
{
    public static int? CrapGrade(CrapSummary? summary, Thresholds thresholds) =>
        summary is null ? null : Scale(summary.Score, thresholds.CrapGood, thresholds.CrapBad);

    /// <summary>A tested file with no mutation sites cannot be improved by tests: best grade.</summary>
    public static int? MutationGrade(MutationSummary? summary, Thresholds thresholds) =>
        summary is null ? null : summary.Score is { } score ? Scale(score, thresholds.MutationGood, thresholds.MutationBad) : 10;

    /// <summary>A parent takes the worst grade of its children for each metric.</summary>
    public static Grades Worst(IEnumerable<Grades> children, bool missingIsWorst)
    {
        var list = children.ToList();
        return new Grades(Worst(list.Select(g => g.Crap), missingIsWorst), Worst(list.Select(g => g.Mutation), missingIsWorst));
    }

    public static Grades ForType(TypeNode type, MetricsSet metrics, Thresholds thresholds) =>
        new(CrapGrade(TypeMetrics.Crap(type, metrics.Crap), thresholds),
            MutationGrade(TypeMetrics.Mutation(type, metrics.Mutation), thresholds));

    /// <summary>Linear from <paramref name="bad"/> (1) to <paramref name="good"/> (10); works whichever direction is better.</summary>
    public static int Scale(double value, double good, double bad)
    {
        if (good == bad) return value == good ? 10 : 1;
        var fraction = Math.Clamp((value - bad) / (good - bad), 0, 1);
        return (int)Math.Round(1 + 9 * fraction, MidpointRounding.AwayFromZero);
    }

    private static int? Worst(IEnumerable<int?> grades, bool missingIsWorst)
    {
        int? worst = null;
        foreach (var grade in grades)
        {
            if (grade is null)
            {
                if (missingIsWorst) return 1;
                continue;
            }
            worst = worst is null ? grade : Math.Min(worst.Value, grade.Value);
        }
        return worst;
    }
}

public static class TypeMetrics
{
    public static CrapSummary? Crap(TypeNode type, CrapSnapshot snapshot)
    {
        var entries = type.Members
            .Where(m => m.Complexity is not null)
            .Select(m => (Member: m, Entry: snapshot.Members.GetValueOrDefault(m.Id)))
            .ToList();
        var scores = entries.Where(x => x.Entry?.Crap is not null).Select(x => x.Entry!.Crap!.Value).ToList();
        if (scores.Count == 0) return null;
        var mu = scores.Average();
        var sigma = Math.Sqrt(scores.Average(s => (s - mu) * (s - mu)));
        var stale = entries.Any(x => x.Entry?.Hash != x.Member.Hash);
        return new CrapSummary(mu, scores.Max(), sigma, scores.Count, stale);
    }

    public static MutationSummary? Mutation(TypeNode type, MutationSnapshot snapshot)
    {
        if (!type.Files.Any(snapshot.Tested)) return null;
        var members = type.Members
            .Select(m => (Member: m, Entry: snapshot.Members.GetValueOrDefault(m.Id)))
            .Where(x => x.Member.Complexity is not null || x.Member.HasInitializer || x.Entry is not null)
            .ToList();
        var entries = members.Select(x => x.Entry)
            .Append(snapshot.Members.GetValueOrDefault(MutationMapper.InitializerId(type)))
            .OfType<MutationEntry>().ToList();
        if (entries.Count == 0) return null;
        var stale = members.Any(x => x.Entry?.Hash != x.Member.Hash);
        return new MutationSummary(
            entries.Sum(x => x.Detected),
            entries.Sum(x => x.Survived),
            entries.Sum(x => x.Uncovered),
            entries.Sum(x => x.Sites),
            stale);
    }
}
