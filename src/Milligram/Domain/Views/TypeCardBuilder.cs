using Milligram.Domain.Hierarchy;
using Milligram.Domain.Metrics;
using Milligram.Domain.Model;
using Milligram.Domain.Policies;

namespace Milligram.Domain.Views;

/// <summary>The detail card for one type: every member with its metrics, and its dependencies both ways.</summary>
public static class TypeCardBuilder
{
    public static TypeCard? Build(CodeModel model, Policy policy, MetricsSet metrics, DiagramTree tree, string typeId)
    {
        var type = tree.Type(typeId) ?? model.Types.FirstOrDefault(t => t.Id == typeId);
        if (type is null) return null;

        var members = type.Members
            .OrderBy(m => m.Span.File, StringComparer.Ordinal).ThenBy(m => m.Span.Start)
            .Select(m => Member(m, metrics))
            .ToList();
        if (metrics.Mutation.Members.GetValueOrDefault(MutationMapper.InitializerId(type)) is { Sites: > 0 } init)
            members.Add(Initializers(type, init));

        var edges = EdgeRules.Apply(model.Edges, policy, id => NameOf(model, policy, id));
        return new TypeCard(
            type.Id,
            type.Name,
            type.Namespace,
            NamePath.RelativeName(type, policy.Prefix),
            type.Kind,
            ViewBuilder.StereotypeOf(type),
            type.Visibility,
            tree.LevelOfType(type.Id),
            type.Spans,
            TypeMetrics.Crap(type, metrics.Crap),
            TypeMetrics.Mutation(type, metrics.Mutation),
            type.Files.Any(metrics.Mutation.Tested),
            Grading.ForType(type, metrics, policy.Thresholds),
            members,
            Dependencies(edges.Where(e => e.From == type.Id), e => e.To, model, policy, tree),
            Dependencies(edges.Where(e => e.To == type.Id), e => e.From, model, policy, tree));
    }

    private static CardMember Member(MemberNode member, MetricsSet metrics)
    {
        var crap = metrics.Crap.Members.GetValueOrDefault(member.Id);
        var mutation = metrics.Mutation.Members.GetValueOrDefault(member.Id);
        return new CardMember(
            member.Id, member.Name, member.Signature, member.Kind, member.Visibility, member.IsStatic, member.IsAbstract,
            member.Span.File, member.Span.StartLine, member.Span.EndLine, member.Complexity,
            crap, crap is not null && crap.Hash != member.Hash,
            mutation, mutation is not null && mutation.Hash != member.Hash);
    }

    private static CardMember Initializers(TypeNode type, MutationEntry entry) =>
        new(MutationMapper.InitializerId(type), "(initializers)", "field and property initializers", MemberKind.Initializer,
            Visibility.Private, false, false, type.File, type.Spans[0].StartLine, type.Spans[0].EndLine, null, null, false, entry, false);

    private static List<CardDependency> Dependencies(
        IEnumerable<DependencyEdge> edges, Func<DependencyEdge, string> other, CodeModel model, Policy policy, DiagramTree tree) =>
        edges
            .Select(e => new CardDependency(
                other(e),
                NameOf(model, policy, other(e)),
                e.Kind,
                DependencyRule.IsViolating(e.Kind, tree.LevelOfType(e.From), tree.LevelOfType(e.To)),
                e.Count,
                other(e).StartsWith(CodeModel.ForeignIdPrefix, StringComparison.Ordinal)))
            .OrderBy(d => d.IsForeign).ThenBy(d => d.Label, StringComparer.Ordinal)
            .ToList();

    private static string NameOf(CodeModel model, Policy policy, string id) =>
        model.Types.FirstOrDefault(t => t.Id == id) is { } type ? NamePath.RelativeName(type, policy.Prefix)
        : model.Foreign.FirstOrDefault(f => f.Id == id)?.Label ?? id;
}
