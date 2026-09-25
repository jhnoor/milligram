using Milligram.Domain.Model;

namespace Milligram.Domain.Metrics;

/// <summary>Attributes mutants to the members that contain them and merges the result into a snapshot.</summary>
public static class MutationMapper
{
    /// <summary>Pseudo-member for mutants outside any member (field initializers, attributes).</summary>
    public static string InitializerId(TypeNode type) => type.Id + ".<init>";

    public static MutationSnapshot Merge(
        MutationSnapshot old,
        CodeModel model,
        IEnumerable<Mutant> mutants,
        IReadOnlyCollection<string> testedMemberIds,
        IReadOnlyCollection<string> testedFiles,
        DateTimeOffset now)
    {
        var hashes = model.Types.SelectMany(t => t.Members).ToDictionary(m => m.Id, m => m.Hash);
        var liveIds = hashes.Keys.Concat(model.Types.Select(InitializerId)).ToHashSet();
        var members = old.Members.Where(kv => liveIds.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        var fresh = testedMemberIds.ToHashSet();
        foreach (var id in fresh) members[id] = MutationEntry.None(hashes.GetValueOrDefault(id));

        var locator = new MemberLocator(model);
        foreach (var mutant in mutants)
        {
            if (mutant.Status is MutantStatus.Ignored or MutantStatus.CompileError or MutantStatus.Pending) continue;
            if (locator.Find(mutant) is not { } id) continue;
            if (fresh.Add(id)) members[id] = MutationEntry.None(hashes.GetValueOrDefault(id));
            members[id] = members[id].Add(mutant.Status);
        }

        var files = new Dictionary<string, DateTimeOffset>(old.Files);
        foreach (var file in testedFiles) files[file] = now;
        return new MutationSnapshot(now, members, files);
    }

    /// <summary>Members that changed since their last mutation run (or were never run).</summary>
    public static IReadOnlyList<MemberNode> Changed(MutationSnapshot snapshot, IEnumerable<MemberNode> members) =>
        members
            .Where(m => m.Complexity is not null)
            .Where(m => !snapshot.Members.TryGetValue(m.Id, out var entry) || entry.Hash != m.Hash)
            .ToList();

    private sealed class MemberLocator(CodeModel model)
    {
        private readonly ILookup<string, (TypeNode Type, MemberNode Member)> byFile =
            model.Types.SelectMany(t => t.Members.Select(m => (t, m))).ToLookup(x => x.m.Span.File);

        private readonly ILookup<string, (TypeNode Type, SourceSpan Span)> typesByFile =
            model.Types.SelectMany(t => t.Spans.Select(s => (t, s))).ToLookup(x => x.s.File);

        public string? Find(Mutant mutant)
        {
            var member = byFile[mutant.File]
                .Where(x => x.Member.Span.Contains(mutant.Line, mutant.Column))
                .OrderBy(x => x.Member.Span.Length)
                .Select(x => x.Member.Id)
                .FirstOrDefault();
            if (member is not null) return member;
            return typesByFile[mutant.File]
                .Where(x => x.Span.Contains(mutant.Line, mutant.Column))
                .OrderBy(x => x.Span.Length)
                .Select(x => InitializerId(x.Type))
                .FirstOrDefault();
        }
    }
}
