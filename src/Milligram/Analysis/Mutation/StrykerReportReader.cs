using System.Text.Json;
using Milligram.Application;
using Milligram.Domain.Metrics;

namespace Milligram.Analysis.Mutation;

/// <summary>Reads a mutation-testing-elements JSON report (Stryker.NET's json reporter).</summary>
public sealed class StrykerReportReader : IMutationReportReader
{
    public IReadOnlyList<Mutant> Read(string reportFile, string root, string projectDirectory)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(reportFile));
        var report = document.RootElement;
        var baseDirectory = report.TryGetProperty("projectRoot", out var projectRoot) && projectRoot.GetString() is { Length: > 0 } pr
            ? pr
            : projectDirectory;

        var mutants = new List<Mutant>();
        foreach (var file in report.GetProperty("files").EnumerateObject())
        {
            var absolute = Path.IsPathRooted(file.Name) ? file.Name : Path.Combine(baseDirectory, file.Name);
            var relative = Path.GetRelativePath(root, absolute).Replace('\\', '/');
            foreach (var mutant in file.Value.GetProperty("mutants").EnumerateArray())
                mutants.Add(Read(mutant, relative));
        }
        return mutants;
    }

    private static Mutant Read(JsonElement mutant, string file)
    {
        var start = mutant.GetProperty("location").GetProperty("start");
        var status = Enum.TryParse<MutantStatus>(mutant.GetProperty("status").GetString(), ignoreCase: true, out var s) ? s : MutantStatus.Pending;
        var mutator = mutant.TryGetProperty("mutatorName", out var name) ? name.GetString() ?? "" : "";
        return new Mutant(file, start.GetProperty("line").GetInt32(), start.GetProperty("column").GetInt32(), status, mutator);
    }
}
