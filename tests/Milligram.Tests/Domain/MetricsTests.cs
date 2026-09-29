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
        Assert.Equal([mutants[2] with { MemberStartLine = First.Span.StartLine }], snapshot.Gaps[First.Id]);
        Assert.Equal([mutants[3] with { MemberStartLine = Second.Span.StartLine }], snapshot.Gaps[Second.Id]);
        Assert.Equal(2, snapshot.Gaps.Count);
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

    [Fact]
    public void DifferentialRunsReplaceGapsAndPreserveTheMembersTheyDoNotRetest()
    {
        var firstGap = new Mutant("a.cs", 4, 5, MutantStatus.Survived, "Equality") { Replacement = ">=", MemberStartLine = 3 };
        var secondGap = new Mutant("a.cs", 8, 5, MutantStatus.NoCoverage, "Boolean") { Replacement = "false", MemberStartLine = 7 };
        var initial = MutationMapper.Merge(MutationSnapshot.Empty, Model, [firstGap, secondGap], [First.Id, Second.Id], ["a.cs"], DateTimeOffset.UnixEpoch);
        var replacement = firstGap with { Replacement = "<" };

        var rerun = MutationMapper.Merge(initial, Model, [replacement], [First.Id], ["a.cs"], DateTimeOffset.UnixEpoch);
        Assert.Equal([replacement], rerun.Gaps[First.Id]);
        Assert.Equal([secondGap], rerun.Gaps[Second.Id]);
        Assert.Equal([firstGap], initial.Gaps[First.Id]);

        var fixedFirst = MutationMapper.Merge(rerun, Model, [replacement with { Status = MutantStatus.Killed }], [First.Id], ["a.cs"], DateTimeOffset.UnixEpoch);
        Assert.False(fixedFirst.Gaps.ContainsKey(First.Id));
        Assert.Equal([secondGap], fixedFirst.Gaps[Second.Id]);

        var withoutSites = MutationMapper.Merge(rerun, Model, [], [First.Id], ["a.cs"], DateTimeOffset.UnixEpoch);
        Assert.False(withoutSites.Gaps.ContainsKey(First.Id));
        Assert.Equal([secondGap], withoutSites.Gaps[Second.Id]);
    }

    [Fact]
    public void GapsForVanishedMembersDisappearAndInitializerGapsAreReplaced()
    {
        var gap = new Mutant("a.cs", 11, 2, MutantStatus.Survived, "String") { Replacement = "\"\"" };
        var initial = MutationMapper.Merge(MutationSnapshot.Empty, Model, [gap], [First.Id], ["a.cs"], DateTimeOffset.UnixEpoch);
        var initializer = MutationMapper.InitializerId(Model.Types[0]);
        Assert.Equal([gap], initial.Gaps[initializer]);

        var untouched = MutationMapper.Merge(initial, Model, [], [Second.Id], ["a.cs"], DateTimeOffset.UnixEpoch);
        Assert.Equal([gap], untouched.Gaps[initializer]);
        Assert.Equal(1, untouched.Members[initializer].Survived);

        var refreshed = MutationMapper.Merge(initial, Model, [gap with { Status = MutantStatus.Killed }], [First.Id], ["a.cs"], DateTimeOffset.UnixEpoch);
        Assert.Empty(refreshed.Gaps);
        Assert.Empty(MutationMapper.Merge(initial, Build.Model([]), [], [], [], DateTimeOffset.UnixEpoch).Gaps);
    }

    [Fact]
    public void SeveralGapsInOneMemberStaySeparateAndUnmappedMutantsAreIgnored()
    {
        var gap = new Mutant("a.cs", 4, 5, MutantStatus.Survived, "Equality") { Replacement = ">=", MemberStartLine = 3 };
        var other = gap with { Replacement = "<" };
        var result = MutationMapper.Merge(MutationSnapshot.Empty, Model, [gap, other, gap with { File = "outside.cs" }],
            [First.Id], ["a.cs"], DateTimeOffset.UnixEpoch);
        Assert.Equal([gap, other], result.Gaps[First.Id]);
        Assert.Equal(2, result.Members[First.Id].Survived);
    }

    [Theory]
    [InlineData(MutantStatus.Ignored)]
    [InlineData(MutantStatus.CompileError)]
    [InlineData(MutantStatus.RuntimeError)]
    [InlineData(MutantStatus.Pending)]
    public void UntestedMutantsDoNotReplacePreviouslyMeasuredGaps(MutantStatus status)
    {
        var gap = new Mutant("a.cs", 4, 5, MutantStatus.Survived, "Equality");
        var initial = MutationMapper.Merge(MutationSnapshot.Empty, Model, [gap], [First.Id], ["a.cs"], DateTimeOffset.UnixEpoch);

        var refreshed = MutationMapper.Merge(initial, Model, [gap with { Status = status }], [Second.Id], ["a.cs"], DateTimeOffset.UnixEpoch);

        Assert.Equal(initial.Gaps[First.Id], refreshed.Gaps[First.Id]);
        Assert.Equal(initial.Members[First.Id], refreshed.Members[First.Id]);
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

    [Fact]
    public void AddingAnExecutableMemberMakesExistingSummariesStale()
    {
        var measured = Build.Member("App.A", "Measured");
        var added = Build.Member("App.A", "Added");
        var type = Build.Type("App.A", measured, added);
        var crap = new CrapSnapshot(DateTimeOffset.UnixEpoch, new Dictionary<string, CrapEntry>
        {
            [measured.Id] = new(1, 1, 1, 1, 1, measured.Hash),
        });
        var mutation = new MutationSnapshot(DateTimeOffset.UnixEpoch, new Dictionary<string, MutationEntry>
        {
            [measured.Id] = new(2, 0, 0, 0, measured.Hash),
        }, new Dictionary<string, DateTimeOffset> { [measured.Span.File] = DateTimeOffset.UnixEpoch });

        Assert.True(TypeMetrics.Crap(type, crap)!.Stale);
        Assert.True(TypeMetrics.Mutation(type, mutation)!.Stale);
        Assert.False(TypeMetrics.Crap(type with { Members = [measured] }, crap)!.Stale);
        Assert.False(TypeMetrics.Mutation(type with { Members = [measured] }, mutation)!.Stale);
        Assert.Equal(1, TypeMetrics.Crap(type, crap)!.Count);
        Assert.Equal(2, TypeMetrics.Mutation(type, mutation)!.Sites);
    }

    [Fact]
    public void ANewTypeInATestedFileIsUnknownRatherThanPerfect()
    {
        var type = Build.Type("App.New", Build.Member("App.New", "Added", file: "shared.cs"));
        var mutation = new MutationSnapshot(DateTimeOffset.UnixEpoch, new Dictionary<string, MutationEntry>
        {
            ["App.Old.Measured()"] = new(2, 0, 0, 0, "h"),
        }, new Dictionary<string, DateTimeOffset> { ["shared.cs"] = DateTimeOffset.UnixEpoch });

        Assert.Null(TypeMetrics.Mutation(type, mutation));
        Assert.Null(Grading.ForType(type, new MetricsSet(CrapSnapshot.Empty, mutation), Defaults).Mutation);
        Assert.Null(TypeMetrics.Mutation(type with { Members = [] }, mutation));
    }

    [Fact]
    public void AMeasuredMethodWithoutMutationSitesStillHasABestGrade()
    {
        var member = Build.Member("App.A", "Measured");
        var type = Build.Type("App.A", member);
        var mutation = new MutationSnapshot(DateTimeOffset.UnixEpoch,
            new Dictionary<string, MutationEntry> { [member.Id] = MutationEntry.None(member.Hash) },
            new Dictionary<string, DateTimeOffset> { [member.Span.File] = DateTimeOffset.UnixEpoch });

        Assert.Equal(10, Grading.MutationGrade(TypeMetrics.Mutation(type, mutation), Defaults));
        Assert.False(TypeMetrics.Mutation(type, mutation)!.Stale);
    }

    [Fact]
    public void MembersWithoutCodeDoNotMakeSummariesStaleButMeasuredFieldsDo()
    {
        var method = Build.Member("App.A", "Measured");
        var field = Build.Member("App.A", "Field", complexity: null);
        var type = Build.Type("App.A", method, field);
        var crap = new CrapSnapshot(DateTimeOffset.UnixEpoch,
            new Dictionary<string, CrapEntry> { [method.Id] = new(1, 1, 1, 1, 1, method.Hash) });
        var entries = new Dictionary<string, MutationEntry> { [method.Id] = new(2, 0, 0, 0, method.Hash) };
        var mutation = new MutationSnapshot(DateTimeOffset.UnixEpoch, entries,
            new Dictionary<string, DateTimeOffset> { [method.Span.File] = DateTimeOffset.UnixEpoch });

        Assert.False(TypeMetrics.Crap(type, crap)!.Stale);
        Assert.False(TypeMetrics.Mutation(type, mutation)!.Stale);
        entries[field.Id] = new(0, 0, 1, 0, "before");
        Assert.True(TypeMetrics.Mutation(type, mutation)!.Stale);
        Assert.Equal(3, TypeMetrics.Mutation(type, mutation)!.Sites);
    }

    [Fact]
    public void AnUninstrumentedMemberIsCurrentWhenItsHashMatches()
    {
        var method = Build.Member("App.A", "Measured");
        var uninstrumented = Build.Member("App.A", "Uninstrumented");
        var type = Build.Type("App.A", method, uninstrumented);
        var entries = new Dictionary<string, CrapEntry>
        {
            [method.Id] = new(1, 1, 1, 1, 1, method.Hash),
            [uninstrumented.Id] = new(1, null, null, 0, 0, uninstrumented.Hash),
        };
        var crap = new CrapSnapshot(DateTimeOffset.UnixEpoch, entries);

        Assert.False(TypeMetrics.Crap(type, crap)!.Stale);
        entries[uninstrumented.Id] = entries[uninstrumented.Id] with { Hash = "before" };
        Assert.True(TypeMetrics.Crap(type, crap)!.Stale);
        Assert.Null(TypeMetrics.Crap(type with { Members = [uninstrumented] }, crap));
    }

    [Fact]
    public void PartialTypesKeepMeasuredResultsAndFlagTheirUnmeasuredParts()
    {
        var first = Build.Member("App.A", "First", file: "first.cs");
        var second = Build.Member("App.A", "Second", file: "first.cs");
        var added = Build.Member("App.A", "Added", file: "second.cs");
        var type = Build.Type("App.A", first, second, added) with { Spans = [Build.Span("first.cs", 1, 5), Build.Span("second.cs", 1, 5)] };
        var snapshot = new MutationSnapshot(DateTimeOffset.UnixEpoch, new Dictionary<string, MutationEntry>
        {
            [first.Id] = new(1, 1, 2, 3, first.Hash),
            [second.Id] = new(4, 0, 5, 6, second.Hash),
            [MutationMapper.InitializerId(type)] = new(2, 0, 1, 1, null),
        }, new Dictionary<string, DateTimeOffset> { ["first.cs"] = DateTimeOffset.UnixEpoch });

        Assert.Equal(new MutationSummary(8, 8, 10, 26, true), TypeMetrics.Mutation(type, snapshot));
    }

    [Fact]
    public void CrapSpreadIncludesEveryMemberRatherThanJustTheNearestToTheMean()
    {
        var members = new[] { Build.Member("App.A", "A"), Build.Member("App.A", "B"), Build.Member("App.A", "C") };
        double[] scores = [1, 2, 6];
        var snapshot = new CrapSnapshot(DateTimeOffset.UnixEpoch,
            members.Select((m, i) => (m.Id, Entry: new CrapEntry(1, 1, scores[i], 1, 1, m.Hash))).ToDictionary(x => x.Id, x => x.Entry));

        var summary = TypeMetrics.Crap(Build.Type("App.A", members), snapshot)!;

        Assert.Equal(3, summary.Mu);
        Assert.Equal(6, summary.Max);
        Assert.Equal(Math.Sqrt(14.0 / 3), summary.Sigma, 6);
    }

    [Theory]
    [InlineData(4, 1)]
    [InlineData(5, 10)]
    [InlineData(6, 1)]
    public void EqualThresholdsGiveOnlyTheirExactValueTheBestGrade(double value, int grade) =>
        Assert.Equal(grade, Grading.Scale(value, 5, 5));
}
