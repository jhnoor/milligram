using System.Diagnostics;
using System.Globalization;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Milligram.Application;
using Milligram.Domain.Model;

namespace Milligram.Analysis.CSharp;

/// <summary>
/// Scans C# with Roslyn. All source files go into one ad-hoc compilation (no MSBuild needed), referencing
/// the framework and restored NuGet packages, so names bind across projects in the source tree.
/// </summary>
public sealed class CSharpScanner : ILanguageScanner
{
    private static readonly CSharpParseOptions ParseOptions = new(
        LanguageVersion.Preview,
        DocumentationMode.None,
        preprocessorSymbols: ["DEBUG", "TRACE", "NET", "NETCOREAPP", "NET5_0_OR_GREATER", "NET6_0_OR_GREATER", "NET8_0_OR_GREATER"]);

    private static readonly CSharpCompilationOptions CompilationOptions = new(
        OutputKind.ConsoleApplication,
        nullableContextOptions: NullableContextOptions.Enable,
        allowUnsafe: true,
        concurrentBuild: true);

    private static readonly string[] SdkUsings =
        ["System", "System.Collections.Generic", "System.IO", "System.Linq", "System.Net.Http", "System.Threading", "System.Threading.Tasks"];

    private static readonly string[] WebSdkUsings =
    [
        "System.Net.Http.Json", "Microsoft.AspNetCore.Builder", "Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Http",
        "Microsoft.AspNetCore.Routing", "Microsoft.Extensions.Configuration", "Microsoft.Extensions.DependencyInjection",
        "Microsoft.Extensions.Hosting", "Microsoft.Extensions.Logging",
    ];

    public CodeModel Scan(ScanRequest request, Action<string>? progress = null)
    {
        Action<string> report = progress ?? (_ => { });
        var clock = Stopwatch.StartNew();
        var files = SourceFiles.Find(request.SourceDirectory, request.Root, request.Exclude);
        var src = SourceFiles.Relative(request.Root, request.SourceDirectory);
        report($"Scanning {files.Count} source file(s)" + (src == "." ? "." : $" under {src}."));

        var parsing = new ScanStage(report, "Parsed", files.Count, "files");
        var trees = files.AsParallel().AsOrdered().Select(file => { var tree = Parse(file); parsing.Tick(); return tree; }).ToList();
        var projects = ProjectDirectories(files, request.Root);
        var compilation = CSharpCompilation.Create(
            "milligram-scan",
            trees.Concat(GlobalUsings(projects)),
            References.For(projects, report),
            CompilationOptions);

        var binding = new ScanStage(report, "Bound", trees.Count, "files");
        var types = new TypeCollector(compilation, request.Root).Collect(trees, binding.Tick, report);
        var linking = new ScanStage(report, "Linked", types.Count, "types");
        var dependencies = new DependencyCollector(types, request.Foreign, request.DiscoverForeign);
        foreach (var type in types.Values)
        {
            dependencies.Collect(type);
            // Semantic models cache bound bodies; remaining types retain the models they still need.
            type.Parts.Clear();
            linking.Tick();
        }

        var edges = dependencies.Edges;
        report($"Scanned {types.Count} types and {edges.Count} dependencies in {Elapsed(clock.Elapsed)}.");
        return new CodeModel(
            request.Title,
            request.Prefix,
            DateTimeOffset.UtcNow,
            types.Values.Select(t => t.ToNode()).OrderBy(t => t.Id, StringComparer.Ordinal).ToList(),
            dependencies.Foreign,
            edges);
    }

    /// <summary>Formatted invariantly, so a scan reads the same on every machine.</summary>
    internal static string Elapsed(TimeSpan taken) =>
        taken.TotalSeconds < 1
            ? $"{(int)taken.TotalMilliseconds} ms"
            : $"{taken.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)} s";

    private static SyntaxTree Parse(string file) =>
        CSharpSyntaxTree.ParseText(SourceText.From(File.ReadAllText(file)), ParseOptions, file);

    /// <summary>The nearest directory with a .csproj above each source file, up to the root.</summary>
    private static IReadOnlyList<string> ProjectDirectories(IEnumerable<string> files, string root)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        var checkedDirectories = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var directory in files.Select(Path.GetDirectoryName).OfType<string>().Distinct())
            if (Owner(directory, root, checkedDirectories) is { } owner) found.Add(owner);
        return found.Order(StringComparer.Ordinal).ToList();
    }

    private static string? Owner(string directory, string root, Dictionary<string, string?> cache)
    {
        if (cache.TryGetValue(directory, out var known)) return known;
        string? owner;
        if (Directory.EnumerateFiles(directory, "*.csproj").Any()) owner = directory;
        else
        {
            var parent = Path.GetDirectoryName(directory);
            owner = parent is null || !parent.StartsWith(root, StringComparison.Ordinal) ? null : Owner(parent, root, cache);
        }
        cache[directory] = owner;
        return owner;
    }

    /// <summary>Implicit usings: the SDK-generated GlobalUsings.g.cs when the project was built, else the SDK defaults.</summary>
    private static IEnumerable<SyntaxTree> GlobalUsings(IReadOnlyList<string> projects)
    {
        var generated = new List<SyntaxTree>();
        var fallback = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in projects)
        {
            var obj = Path.Combine(project, "obj");
            var file = Directory.Exists(obj)
                ? Directory.EnumerateFiles(obj, "*.GlobalUsings.g.cs", new EnumerationOptions
                { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false }).MaxBy(File.GetLastWriteTimeUtc)
                : null;
            if (file is not null) generated.Add(Parse(file));
            else foreach (var ns in DefaultUsings(project)) fallback.Add(ns);
        }
        if (projects.Count == 0) fallback.UnionWith(SdkUsings);
        if (fallback.Count > 0)
            generated.Add(CSharpSyntaxTree.ParseText(string.Concat(fallback.Select(ns => $"global using global::{ns};\n")), ParseOptions, "milligram.usings.g.cs"));
        return generated;
    }

    private static IEnumerable<string> DefaultUsings(string project)
    {
        var csproj = Directory.EnumerateFiles(project, "*.csproj").Order(StringComparer.Ordinal).First();
        try
        {
            var root = XDocument.Load(csproj).Root;
            if (root is null) return [];
            var enabled = root.Elements().Where(e => e.Name.LocalName == "PropertyGroup")
                .Elements().Where(e => e.Name.LocalName.Equals("ImplicitUsings", StringComparison.OrdinalIgnoreCase) &&
                    !e.AncestorsAndSelf().Any(a => !string.IsNullOrWhiteSpace(a.Attribute("Condition")?.Value)))
                .LastOrDefault()?.Value;
            if (!string.Equals(enabled, "enable", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase)) return [];
            var sdks = (root.Attribute("Sdk")?.Value ?? "").Split(';')
                .Concat(root.Elements().Where(e => e.Name.LocalName == "Sdk").Select(e => e.Attribute("Name")?.Value ?? ""));
            return sdks.Any(sdk => sdk.Split('/')[0].Trim().Equals("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase))
                ? [.. SdkUsings, .. WebSdkUsings] : SdkUsings;
        }
        catch (Exception e) when (e is System.Xml.XmlException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
