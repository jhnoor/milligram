using System.Diagnostics;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Milligram.Application;
using Milligram.Domain.Model;
using Milligram.Domain.Policies;

namespace Milligram.Analysis.CSharp;

/// <summary>A compiler context returned by project evaluation; references retain their own compilations.</summary>
public sealed record ProjectCompilation(string Path, string Name, CSharpCompilation Compilation)
{
    public IScanInputs? Inputs { get; init; }
}

/// <summary>Draws evaluated project inputs without merging their symbols, imports or framework references.</summary>
public sealed class ProjectScanner(
    Func<IReadOnlyList<string>, string?, Action<string>, Task<IReadOnlyList<ProjectCompilation>>> evaluate,
    IReadOnlyList<string> projectFiles,
    string? configuration = null,
    Func<IScanInputs?>? inputState = null) : ILanguageScanner
{
    private IScanInputs? inputs;
    public IScanInputs? Inputs => inputState?.Invoke() ?? inputs;

    public CodeModel Scan(ScanRequest request, Action<string>? progress = null)
    {
        Action<string> report = progress ?? (_ => { });
        var clock = Stopwatch.StartNew();
        var paths = new ProjectPaths(request.Root);
        var files = projectFiles.Select(paths.Absolute).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (files.Count == 0) throw new MilligramException("Project evaluation needs at least one --msbuild FILE.csproj.");
        foreach (var file in files)
            if (!paths.Contains(file) || !File.Exists(file) || !file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                throw new MilligramException($"--msbuild must name an existing C# project inside the project root: {file}");

        IReadOnlyList<ProjectCompilation> projects;
        try { projects = evaluate(files, configuration, report).GetAwaiter().GetResult(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            throw new MilligramException($"MSBuild evaluation failed: {error.Message} Fix the project/SDK, or run `milligram ir --source-only` for the approximate source-only scan (set scan.mode to sourceOnly for startup). The previous model was not replaced.");
        }
        if (projects.Count == 0) throw new MilligramException("MSBuild returned no C# projects. The previous model was not replaced; `milligram ir --source-only` uses the source-only fallback.");
        inputs = projects.Select(project => project.Inputs).FirstOrDefault(input => input is not null);
        return Collect(projects, request with { Scan = request.Scan with { Configuration = configuration } }, report, clock);
    }

    internal static CodeModel Collect(IReadOnlyList<ProjectCompilation> projects, ScanRequest request, Action<string> report, Stopwatch? clock = null)
    {
        var paths = new ProjectPaths(request.Root);
        var excludes = request.Exclude.Select(Glob.ToRegex).ToList();
        var collected = new List<(ProjectCompilation Project, IReadOnlyDictionary<INamedTypeSymbol, TypeAccumulator> Types)>();
        foreach (var project in projects.OrderBy(p => p.Path, StringComparer.Ordinal).ThenBy(p => p.Name, StringComparer.Ordinal))
        {
            if (!paths.Contains(project.Path))
            {
                report($"Reference project outside the root is used for binding only: {project.Path}");
                continue;
            }
            var trees = project.Compilation.SyntaxTrees.Where(tree =>
            {
                if (!Path.IsPathFullyQualified(tree.FilePath) || !paths.Contains(tree.FilePath) || !File.Exists(tree.FilePath))
                {
                    report($"Source outside the root or without a physical path is used for binding only: {tree.FilePath}");
                    return false;
                }
                return !excludes.Any(exclude => exclude.IsMatch(paths.Relative(tree.FilePath)));
            }).OrderBy(tree => tree.FilePath, StringComparer.Ordinal).ToList();
            report($"Binding {trees.Count} evaluated source file(s) from {paths.Relative(project.Path)} ({project.Name}).");
            var stage = new ScanStage(report, "Bound", trees.Count, "files");
            var types = new TypeCollector(project.Compilation, request.Root).Collect(trees, stage.Tick, report, evaluated: true);
            collected.Add((project, types));
        }

        foreach (var group in collected.SelectMany(item => item.Types.Values.Select(type => (item.Project, Type: type)))
            .GroupBy(item => item.Type.Id, StringComparer.Ordinal).Where(group => group.Count() > 1))
            foreach (var item in group)
                item.Type.QualifyProject(paths.Relative(item.Project.Path) + "[" + item.Project.Name + "]");

        var edges = new List<DependencyEdge>();
        var foreign = new Dictionary<string, ForeignNode>(StringComparer.Ordinal);
        var nodes = new List<TypeNode>();
        foreach (var (project, types) in collected)
        {
            var context = MetricContext(project, paths, request.Scan.Configuration);
            var visible = new Dictionary<INamedTypeSymbol, TypeAccumulator>(types, SymbolEqualityComparer.Default);
            foreach (var reference in project.Compilation.References.OfType<CompilationReference>())
            {
                var target = collected.FirstOrDefault(item => SymbolEqualityComparer.Default.Equals(item.Project.Compilation.Assembly, reference.Compilation.Assembly));
                if (target.Types is null || project.Compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol assembly) continue;
                foreach (var type in target.Types.Values)
                {
                    var ns = SymbolNames.Namespace(type.Symbol);
                    var name = ns.Length == 0 ? type.Symbol.MetadataName : ns + "." + type.Symbol.MetadataName;
                    if (assembly.GetTypeByMetadataName(name) is { } symbol) visible[symbol] = type;
                }
            }
            var dependencies = new DependencyCollector(visible, request.Foreign, request.DiscoverForeign);
            var stage = new ScanStage(report, "Linked", types.Count, "types");
            foreach (var type in types.Values)
            {
                dependencies.Collect(type);
                type.Parts.Clear();
                var node = type.ToNode();
                nodes.Add(node with { Members = node.Members.Select(member => member with { Hash = MemberReader.Hash(context + member.Hash) }).ToList() });
                stage.Tick();
            }
            edges.AddRange(dependencies.Edges);
            foreach (var node in dependencies.Foreign) foreign.TryAdd(node.Id, node);
        }
        report($"Scanned {nodes.Count} types and {edges.Count} dependencies from {collected.Count} project context(s)" +
            (clock is null ? "." : $" in {CSharpScanner.Elapsed(clock.Elapsed)}."));
        return new CodeModel(request.Title, request.Prefix, DateTimeOffset.UtcNow,
            nodes.OrderBy(type => type.Id, StringComparer.Ordinal).ToList(),
            foreign.Values.OrderBy(node => node.Id, StringComparer.Ordinal).ToList(),
            edges.OrderBy(edge => edge.From, StringComparer.Ordinal).ThenBy(edge => edge.To, StringComparer.Ordinal).ToList());
    }

    /// <summary>Identical source can execute differently under another compiler context; old metrics must become stale.</summary>
    private static string MetricContext(ProjectCompilation project, ProjectPaths paths, string? configuration) =>
        MemberReader.Hash(JsonSerializer.Serialize(new
        {
            Project = paths.Relative(project.Path),
            project.Name,
            Configuration = configuration,
            project.Compilation.Options.OptimizationLevel,
            project.Compilation.Options.CheckOverflow,
            project.Compilation.Options.Platform,
            project.Compilation.Options.NullableContextOptions,
            Symbols = project.Compilation.SyntaxTrees.SelectMany(tree => tree.Options.PreprocessorSymbolNames).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal),
            References = project.Compilation.ReferencedAssemblyNames.Select(assembly => assembly.ToString()).Order(StringComparer.Ordinal),
        }, MilligramJson.Options));
}
