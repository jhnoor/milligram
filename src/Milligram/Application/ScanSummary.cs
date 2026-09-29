namespace Milligram.Application;

/// <summary>
/// What the last scan drew, and the one thing worth running next. Printed after `ir` and after `serve`'s first
/// scan, so a new user never has to read the README to find out why every box is grey.
/// </summary>
public sealed record ScanSummary(int Types, int Namespaces, int RedArrows, bool HasCrap, bool HasMutation)
{
    public static ScanSummary Of(Workspace workspace)
    {
        var model = workspace.Model;
        var metrics = workspace.Metrics;
        return new ScanSummary(
            model.Types.Count,
            model.Types.Select(t => t.Namespace).Distinct(StringComparer.Ordinal).Count(),
            workspace.View(null, null).Edges.Count(e => e.Violating),
            !metrics.Crap.IsEmpty,
            metrics.Mutation.Files.Count > 0);
    }

    /// <summary>One line for what is on screen, one for what to do about it.</summary>
    public IEnumerable<string> Describe()
    {
        if (Types == 0)
        {
            yield return "No types found. Check \"src\" and \"exclude\" in milligram.json, then rerun.";
            yield break;
        }
        yield return $"{Count(Types, "type")} in {Count(Namespaces, "namespace")}, {Arrows}.";
        yield return $"Next: {Next}";
    }

    private string Arrows => RedArrows == 0 ? "no red arrows" : Count(RedArrows, "red arrow");

    private string Next =>
        !HasCrap ? "`milligram crap` to colour the boxes by complexity × missing coverage (runs your tests)."
        : !HasMutation ? "`milligram mutate` to score how well those tests kill mutants."
        : RedArrows > 0 ? "open the viewer and follow the red arrows: they point the wrong way."
        : "open the viewer and start with the reddest boxes.";

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
}
