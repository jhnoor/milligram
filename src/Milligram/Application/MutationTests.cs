using Milligram.Domain.Model;

namespace Milligram.Application;

/// <summary>Stryker falls back to another test framework when the requested library framework is absent.</summary>
public static class MutationTests
{
    public static IReadOnlyList<CompilerContext> Select(CompilerContext owner, IReadOnlyList<MetricTestProject> tests) => tests
        .GroupBy(test => test.Context.Project, StringComparer.Ordinal)
        .Select(group =>
        {
            var exact = group.Where(test => owner.Framework is not null && test.Context.Framework == owner.Framework).ToList();
            var possible = exact.Count > 0 ? exact : group.ToList();
            return possible.All(test => test.Context.Resolved && test.References.Contains(owner)) ? possible[0].Context : null;
        })
        .OfType<CompilerContext>().OrderBy(test => test.Project, StringComparer.Ordinal).ToList();
}
