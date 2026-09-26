using Milligram.Domain.Hierarchy;
using Milligram.Domain.Metrics;
using Milligram.Domain.Model;
using Milligram.Domain.Policies;
using Milligram.Domain.Views;

namespace Milligram.Application;

/// <summary>
/// The examined project as the viewer sees it: current policy, model, and metrics, reloaded when their files change.
/// </summary>
public sealed class Workspace
{
    private readonly Lock gate = new();
    private readonly Lock scanGate = new();
    private readonly ILanguageScanner scanner;
    private readonly Dictionary<string, DiagramTree> trees = [];
    private Policy policy = new();
    private CodeModel model;
    private MetricsSet metrics = MetricsSet.Empty;

    public Workspace(ProjectPaths paths, ILanguageScanner scanner)
    {
        Paths = paths;
        this.scanner = scanner;
        ToAgent = new Mailbox(paths.ToAgentDirectory);
        ToViewer = new Mailbox(paths.ToViewerDirectory);
        model = CodeModel.Empty(DefaultTitle, "");
    }

    public ProjectPaths Paths { get; }
    public Mailbox ToAgent { get; }
    public Mailbox ToViewer { get; }
    public string DefaultTitle => Path.GetFileName(Paths.Root);
    public string? PolicyError { get; private set; }
    public long Version { get; private set; }

    public Policy Policy { get { lock (gate) return policy; } }
    public CodeModel Model { get { lock (gate) return model; } }
    public MetricsSet Metrics { get { lock (gate) return metrics; } }

    public void Load()
    {
        ReloadPolicy();
        ReloadModel();
        ReloadMetrics();
    }

    /// <summary>Re-reads milligram.json. A broken file keeps the last good policy and reports the error.</summary>
    public bool ReloadPolicy()
    {
        try
        {
            var loaded = JsonFile.Read<Policy>(Paths.PolicyFile) ?? new Policy();
            Update(() => { policy = loaded; PolicyError = null; });
            return true;
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or IOException or NotSupportedException)
        {
            Update(() => PolicyError = $"milligram.json: {e.Message}");
            return false;
        }
    }

    public void ReloadModel()
    {
        var loaded = TryRead<CodeModel>(Paths.ModelFile);
        if (loaded is not null) Update(() => model = loaded);
    }

    public void ReloadMetrics()
    {
        var crap = TryRead<CrapSnapshot>(Paths.CrapFile) ?? CrapSnapshot.Empty;
        var mutation = TryRead<MutationSnapshot>(Paths.MutationFile) ?? MutationSnapshot.Empty;
        Update(() => metrics = new MetricsSet(crap, mutation));
    }

    /// <summary>Scans the source with the current policy and writes .milligram/model.json.</summary>
    public CodeModel Generate()
    {
        lock (scanGate)
        {
            var current = Policy;
            var request = new ScanRequest(
                Paths.Root,
                Paths.Absolute(current.Src),
                current.Exclude,
                current.Prefix,
                current.Foreign,
                current.Title ?? DefaultTitle);
            var scanned = scanner.Scan(request);
            JsonFile.Write(Paths.ModelFile, scanned);
            Update(() => model = scanned);
            return scanned;
        }
    }

    /// <summary>
    /// Applies a viewer edit. Only the keys it changes are rewritten, so comments and layout in milligram.json
    /// survive; a file that does not parse is left alone and the edit is refused.
    /// </summary>
    public Policy EditPolicy(Func<Policy, Policy> edit)
    {
        lock (gate)
        {
            var edited = edit(policy);
            var exists = File.Exists(Paths.PolicyFile);
            try
            {
                var text = PolicyText.Edit(exists ? JsonFile.ReadText(Paths.PolicyFile) : "{\n}\n", exists ? policy : new Policy(), edited);
                JsonFile.WriteText(Paths.PolicyFile, text);
            }
            catch (System.Text.Json.JsonException e)
            {
                throw new MilligramException($"Fix milligram.json before editing it from the viewer: {e.Message}");
            }
            policy = edited;
            PolicyError = null;
            Invalidate();
            return edited;
        }
    }

    /// <summary>Snapshots are written with keys in ordinal order so reruns produce small, local diffs.</summary>
    public void SaveMetrics(CrapSnapshot crap)
    {
        var sorted = crap with { Members = Sorted(crap.Members) };
        Save(Paths.CrapFile, sorted, () => metrics = metrics with { Crap = sorted });
    }

    public void SaveMetrics(MutationSnapshot mutation)
    {
        var sorted = mutation with { Members = Sorted(mutation.Members), Files = Sorted(mutation.Files) };
        Save(Paths.MutationFile, sorted, () => metrics = metrics with { Mutation = sorted });
    }

    private static SortedDictionary<string, T> Sorted<T>(IReadOnlyDictionary<string, T> entries) =>
        new(entries.ToDictionary(e => e.Key, e => e.Value), StringComparer.Ordinal);

    public DiagramTree Tree(string? contextId)
    {
        lock (gate)
        {
            var key = contextId is not null && policy.FindProposal(contextId) is not null ? contextId : DiagramTree.RealContext;
            if (!trees.TryGetValue(key, out var tree))
                trees[key] = tree = TreeBuilder.ForContext(model, policy, key);
            return tree;
        }
    }

    public DiagramView View(string? contextId, string? focusId)
    {
        var tree = Tree(contextId);
        lock (gate) return ViewBuilder.Build(model, policy, metrics, tree, focusId);
    }

    public TypeCard? Card(string? contextId, string typeId)
    {
        var tree = Tree(contextId);
        lock (gate) return TypeCardBuilder.Build(model, policy, metrics, tree, typeId);
    }

    /// <summary>The types a view node stands for: a tree node's subtree, or a single type.</summary>
    public IReadOnlyList<TypeNode> TypesOf(string? contextId, string nodeId)
    {
        var tree = Tree(contextId);
        if (nodeId.StartsWith(ViewBuilder.TypeIdPrefix, StringComparison.Ordinal)) nodeId = nodeId[ViewBuilder.TypeIdPrefix.Length..];
        if (tree.Type(nodeId) is { } type) return [type];
        return tree.TryFind(nodeId)?.AllTypes().ToList() ?? [];
    }

    public IReadOnlyList<string> FilesOf(string? contextId, string nodeId) =>
        TypesOf(contextId, nodeId).SelectMany(t => t.Files).Distinct().OrderBy(f => f, StringComparer.Ordinal).ToList();

    private void Save<T>(string path, T value, Action apply)
    {
        JsonFile.Write(path, value);
        Update(apply);
    }

    private void Update(Action apply)
    {
        lock (gate)
        {
            apply();
            Invalidate();
        }
    }

    private void Invalidate()
    {
        trees.Clear();
        Version++;
    }

    private static T? TryRead<T>(string path) where T : class
    {
        try { return JsonFile.Read<T>(path); }
        catch (Exception e) when (e is System.Text.Json.JsonException or IOException or NotSupportedException) { return null; }
    }
}
