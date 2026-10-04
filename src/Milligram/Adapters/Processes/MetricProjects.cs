using Microsoft.CodeAnalysis;
using Milligram.Adapters.Files;
using Milligram.Analysis.CSharp;
using Milligram.Application;

namespace Milligram.Adapters.Processes;

public sealed partial class ProcessRunner : IMetricProjectReader
{
    /// <summary>Test references select the library framework; a test's own framework need not match it.</summary>
    public async Task<IReadOnlyList<MetricTestProject>> ReadAsync(string root, IReadOnlyList<string> tests, string? configuration,
        Action<string> log, CancellationToken cancellation)
    {
        if (tests.Count == 0) return [];
        cancellation.ThrowIfCancellationRequested();
        try
        {
            var projects = await EvaluateProjectsAsync(tests, configuration, log, new EvaluatedInputs()).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            return TestContexts(projects, tests, new ProjectPaths(root));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            throw new MilligramException($"Cannot evaluate metric project ownership: {error.Message} Restore the test projects and check their SDK/configuration.");
        }
    }

    internal static IReadOnlyList<MetricTestProject> TestContexts(IReadOnlyList<ProjectCompilation> projects,
        IReadOnlyList<string> tests, ProjectPaths paths)
    {
        var contexts = projects.Distinct().ToDictionary(project => project, project => project.Context(paths));
        return projects.Where(project => tests.Contains(project.Path, StringComparer.Ordinal))
            .Select(project => new MetricTestProject(contexts[project], Reachable(project).Select(reference => contexts[reference])
                .Distinct().OrderBy(context => context.Project, StringComparer.Ordinal).ThenBy(context => context.Framework, StringComparer.Ordinal).ToList()))
            .ToList();

        IEnumerable<ProjectCompilation> Reachable(ProjectCompilation entry)
        {
            var seen = new HashSet<ProjectCompilation>();
            var pending = new Stack<ProjectCompilation>();
            pending.Push(entry);
            while (pending.TryPop(out var current))
            {
                if (!seen.Add(current)) continue;
                yield return current;
                foreach (var reference in current.Compilation.References.OfType<CompilationReference>())
                    foreach (var target in projects.Where(project => ReferenceEquals(project.Compilation, reference.Compilation)))
                        pending.Push(target);
            }
        }
    }
}
