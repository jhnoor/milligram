using Milligram.Domain.Metrics;
using Milligram.Domain.Model;
using Milligram.Domain.Policies;

namespace Milligram.Tests.Domain;

public class CrapTests
{
    [Theory]
    [InlineData(1, 1.0, 1.0)]
    [InlineData(1, 0.0, 2.0)]
    [InlineData(5, 0.0, 30.0)]
    [InlineData(5, 0.5, 8.125)]
    public void ScoreIsComplexitySquaredTimesUncoveredCubedPlusComplexity(int complexity, double coverage, double expected) =>
        Assert.Equal(expected, Crap.Score(complexity, coverage), 6);

    [Fact]
    public void CoverageIsTheShareOfCoverableLinesHit()
    {
        var member = Build.Member("App.A", "M", complexity: 2, file: "a.cs", startLine: 10, endLine: 14);
        var model = Build.Model([Build.Type("App.A", member)]);
        var hits = new LineHits(new Dictionary<string, IReadOnlyDictionary<int, int>>
        {
            ["a.cs"] = new Dictionary<int, int> { [9] = 5, [10] = 1, [11] = 0, [12] = 3, [13] = 0, [20] = 1 },
        });

        var entry = Crap.Compute(model, hits, DateTimeOffset.UnixEpoch).Members[member.Id];

        Assert.Equal(4, entry.Coverable);
        Assert.Equal(2, entry.Covered);
        Assert.Equal(0.5, entry.Coverage);
        Assert.Equal(Crap.Score(2, 0.5), entry.Crap);
        Assert.Equal("h", entry.Hash);
    }

    [Fact]
    public void AFileMissingFromCoverageIsUncovered()
    {
        var member = Build.Member("App.A", "M", complexity: 3, file: "untested.cs");
        var snapshot = Crap.Compute(Build.Model([Build.Type("App.A", member)]), new LineHits(new Dictionary<string, IReadOnlyDictionary<int, int>>()), DateTimeOffset.UnixEpoch);
        Assert.Equal(0, snapshot.Members[member.Id].Coverage);
        Assert.Equal(12, snapshot.Members[member.Id].Crap);
    }

    [Fact]
    public void MembersWithoutCodeAreNotScored()
    {
        var field = Build.Member("App.A", "field", complexity: null);
        var snapshot = Crap.Compute(Build.Model([Build.Type("App.A", field)]), new LineHits(new Dictionary<string, IReadOnlyDictionary<int, int>>()), DateTimeOffset.UnixEpoch);
        Assert.True(snapshot.IsEmpty);
    }

    [Fact]
    public void AMemberWithNoInstrumentedLinesHasNoScore()
    {
        var member = Build.Member("App.A", "M", complexity: 1, file: "a.cs", startLine: 50, endLine: 52);
        var hits = new LineHits(new Dictionary<string, IReadOnlyDictionary<int, int>> { ["a.cs"] = new Dictionary<int, int> { [1] = 1 } });
        var entry = Crap.Compute(Build.Model([Build.Type("App.A", member)]), hits, DateTimeOffset.UnixEpoch).Members[member.Id];
        Assert.Null(entry.Coverage);
        Assert.Null(entry.Crap);
    }
}

public class MutationMapperTests
{
    private static readonly MemberNode First = Build.Member("App.A", "First", 2, "a.cs", 3, 5, hash: "h1");
    private static readonly MemberNode Second = Build.Member("App.A", "Second", 1, "a.cs", 7, 9, hash: "h2");
    private static readonly CodeModel Model = Build.Model([Build.Type("App.A", First, Second) with { Spans = [Build.Span("a.cs", 1, 12)] }]);

    [Fact]
    public void MutantsCountAgainstTheMemberThatContainsThem()
    {
        Mutant[] mutants =
        [
            new("a.cs", 4, 5, MutantStatus.Killed, "m"),
            new("a.cs", 4, 9, MutantStatus.Timeout, "m"),
            new("a.cs", 5, 1, MutantStatus.Survived, "m"),
            new("a.cs", 8, 2, MutantStatus.NoCoverage, "m"),
            new("a.cs", 8, 3, MutantStatus.CompileError, "m"),
            new("a.cs", 11, 2, MutantStatus.Killed, "m"),
        ];

        var snapshot = MutationMapper.Merge(MutationSnapshot.Empty, Model, mutants, [First.Id, Second.Id], ["a.cs"], DateTimeOffset.UnixEpoch);

        Assert.Equal(new MutationEntry(1, 1, 1, 0, "h1"), snapshot.Members[First.Id]);
        Assert.Equal(new MutationEntry(0, 0, 0, 1, "h2"), snapshot.Members[Second.Id]);
        Assert.Equal(1, snapshot.Members["App.A.<init>"].Killed);
        Assert.True(snapshot.Tested("a.cs"));
    }

    [Fact]
    public void RerunningAMemberReplacesItsOldCounts()
    {
        var old = new MutationSnapshot(DateTimeOffset.UnixEpoch,
            new Dictionary<string, MutationEntry> { [First.Id] = new(5, 0, 5, 5, "old"), [Second.Id] = new(2, 0, 0, 0, "h2") },
            new Dictionary<string, DateTimeOffset>());

        var snapshot = MutationMapper.Merge(old, Model, [new Mutant("a.cs", 4, 1, MutantStatus.Killed, "m")], [First.Id], ["a.cs"], DateTimeOffset.UnixEpoch);

        Assert.Equal(new MutationEntry(1, 0, 0, 0, "h1"), snapshot.Members[First.Id]);
        Assert.Equal(new MutationEntry(2, 0, 0, 0, "h2"), snapshot.Members[Second.Id]);
    }

    [Fact]
    public void EntriesForVanishedMembersAreDropped()
    {
        var old = new MutationSnapshot(DateTimeOffset.UnixEpoch,
            new Dictionary<string, MutationEntry> { ["App.A.Gone()"] = new(1, 0, 0, 0, "x") },
            new Dictionary<string, DateTimeOffset>());
        var snapshot = MutationMapper.Merge(old, Model, [], [], [], DateTimeOffset.UnixEpoch);
        Assert.DoesNotContain("App.A.Gone()", snapshot.Members.Keys);
    }

    [Fact]
    public void ChangedFindsNewAndEditedMembersWithCode()
    {
        var snapshot = new MutationSnapshot(DateTimeOffset.UnixEpoch,
            new Dictionary<string, MutationEntry> { [First.Id] = new(1, 0, 0, 0, "h1"), [Second.Id] = new(1, 0, 0, 0, "stale") },
            new Dictionary<string, DateTimeOffset>());
        var field = Build.Member("App.A", "field", complexity: null);
        var added = Build.Member("App.A", "Added", 1);

        var changed = MutationMapper.Changed(snapshot, [First, Second, field, added]);

        Assert.Equal([Second.Id, added.Id], changed.Select(m => m.Id));
    }
}

public class GradingTests
{
    private static readonly Thresholds Defaults = new();

    [Theory]
    [InlineData(5, 10)]
    [InlineData(1, 10)]
    [InlineData(30, 1)]
    [InlineData(100, 1)]
    [InlineData(17.5, 6)]
    public void CrapGradesFallFromGoodToBad(double score, int expected) =>
        Assert.Equal(expected, Grading.Scale(score, Defaults.CrapGood, Defaults.CrapBad));

    [Theory]
    [InlineData(1.0, 10)]
    [InlineData(0.9, 10)]
    [InlineData(0.5, 1)]
    [InlineData(0.75, 7)]
    public void MutationGradesRiseWithScore(double score, int expected) =>
        Assert.Equal(expected, Grading.Scale(score, Defaults.MutationGood, Defaults.MutationBad));

    [Fact]
    public void ATestedTypeWithoutMutationSitesIsBest() =>
        Assert.Equal(10, Grading.MutationGrade(new MutationSummary(0, 0, 0, 0, false), Defaults));

    [Fact]
    public void UnknownStaysUnknown()
    {
        Assert.Null(Grading.CrapGrade(null, Defaults));
        Assert.Null(Grading.MutationGrade(null, Defaults));
    }

    [Fact]
    public void WorstIgnoresUnknownUnlessMissingIsWorst()
    {
        Grades[] grades = [new(8, null), new(3, 9), Grades.Unknown];
        Assert.Equal(new Grades(3, 9), Grading.Worst(grades, missingIsWorst: false));
        Assert.Equal(new Grades(1, 1), Grading.Worst(grades, missingIsWorst: true));
    }

    [Fact]
    public void TypeCrapSummaryIsMeanMaxAndSpread()
    {
        var a = Build.Member("App.A", "A", 1);
        var b = Build.Member("App.A", "B", 5);
        var type = Build.Type("App.A", a, b);
        var snapshot = new CrapSnapshot(DateTimeOffset.UnixEpoch, new Dictionary<string, CrapEntry>
        {
            [a.Id] = new(1, 1, 2, 1, 1, "h"),
            [b.Id] = new(5, 0, 6, 1, 0, "h"),
        });

        var summary = TypeMetrics.Crap(type, snapshot)!;

        Assert.Equal(4, summary.Mu);
        Assert.Equal(6, summary.Max);
        Assert.Equal(2, summary.Sigma);
        Assert.Equal(6, summary.Score);
    }

    [Fact]
    public void TypeMutationSummaryNeedsATestedFile()
    {
        var member = Build.Member("App.A", "A", 1, "a.cs");
        var type = Build.Type("App.A", member);
        var untested = new MutationSnapshot(DateTimeOffset.UnixEpoch, new Dictionary<string, MutationEntry>(), new Dictionary<string, DateTimeOffset>());
        Assert.Null(TypeMetrics.Mutation(type, untested));

        var tested = new MutationSnapshot(DateTimeOffset.UnixEpoch,
            new Dictionary<string, MutationEntry> { [member.Id] = new(3, 1, 0, 1, "h") },
            new Dictionary<string, DateTimeOffset> { ["a.cs"] = DateTimeOffset.UnixEpoch });
        var summary = TypeMetrics.Mutation(type, tested)!;
        Assert.Equal(4, summary.Killed);
        Assert.Equal(5, summary.Sites);
        Assert.Equal(0.8, summary.Score);
    }
}
