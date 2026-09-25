using System.Text.Json.Nodes;
using Milligram.Adapters.Cli;
using Milligram.Analysis.CSharp;
using Milligram.Application;
using Milligram.Domain.Hierarchy;
using Milligram.Domain.Policies;

namespace Milligram.Tests.Application;

public class PolicyJsonTests
{
    [Fact]
    public void ReadsCommentsTrailingCommasAndBothProposalEntryShapes()
    {
        using var project = new TempProject(("milligram.json", """
            {
              // a comment
              "prefix": "Shop",
              "levels": [["Domain"], ["Web"],],
              "edgeKinds": [{ "from": "A", "to": "B", "kind": "association" }],
              "proposals": [{
                "id": "p1", "name": "Split",
                "layers": [{ "id": "core", "label": "Core", "namespaces": ["Domain", { "id": "io", "label": "IO", "namespaces": ["Web"] }] }]
              }]
            }
            """));

        var policy = JsonFile.Read<Policy>(Path.Combine(project.Root, "milligram.json"))!;

        Assert.Equal("Shop", policy.Prefix);
        Assert.Equal(2, policy.Levels.Count);
        Assert.Equal(Milligram.Domain.Model.EdgeKind.Association, policy.EdgeKinds[0].Kind);
        var entries = policy.Proposals[0].Layers[0].Namespaces;
        Assert.Equal("Domain", entries[0].Namespace);
        Assert.Equal("IO", entries[1].Group!.Label);
        Assert.Equal(Policy.DefaultExclude, policy.Exclude);
    }

    [Fact]
    public void ProposalEntriesRoundTrip()
    {
        using var project = new TempProject();
        var path = Path.Combine(project.Root, "p.json");
        var policy = new Policy
        {
            Proposals = [new Proposal { Id = "p", Layers = [new ProposalGroup { Id = "g", Namespaces = [ProposalEntry.Of("A"), ProposalEntry.Of(new ProposalGroup { Id = "n" })] }] }],
        };
        JsonFile.Write(path, policy);
        var text = File.ReadAllText(path);
        Assert.Contains("\"A\"", text);
        var read = JsonFile.Read<Policy>(path)!;
        Assert.Equal("n", read.Proposals[0].Layers[0].Namespaces[1].Group!.Id);
    }
}

public class MailboxTests
{
    [Fact]
    public void MessagesAreTakenOldestFirstAndRemoved()
    {
        using var project = new TempProject();
        var mailbox = new Mailbox(Path.Combine(project.Root, "mail"));
        mailbox.Post("context", new JsonObject { ["context"] = "real" });
        Thread.Sleep(5);
        mailbox.Post("message", new JsonObject { ["text"] = "hello" });

        Assert.Equal(2, mailbox.Count);
        var peeked = mailbox.Take(keep: true);
        Assert.Equal(["context", "message"], peeked.Select(m => m.Op));
        Assert.Equal("hello", (string?)peeked[1].Data["text"]);
        Assert.Equal(2, mailbox.Take().Count);
        Assert.Empty(mailbox.Take());
    }

    [Fact]
    public void HandWrittenMailWithoutAnEnvelopeIsAccepted()
    {
        using var project = new TempProject(("mail/001.json", """{ "op": "display", "context": "p1" }"""), ("mail/002.json", "not json"));
        var messages = new Mailbox(Path.Combine(project.Root, "mail")).Take();
        Assert.Equal("display", messages[0].Op);
        Assert.Equal("p1", (string?)messages[0].Data["context"]);
        Assert.Equal("invalid", messages[1].Op);
    }

    [Fact]
    public void AMissingMailboxIsEmpty() => Assert.Empty(new Mailbox("/definitely/not/here").Take());
}

public class WorkspaceTests
{
    private const string Source = """
        namespace Shop.Domain { public class Order { } }
        namespace Shop.Web { public class Page { Shop.Domain.Order? order; } }
        """;

    private static Workspace Open(TempProject project)
    {
        var workspace = new Workspace(new ProjectPaths(project.Root), new CSharpScanner());
        workspace.Load();
        return workspace;
    }

    [Fact]
    public void GenerateScansWritesTheModelAndBuildsViews()
    {
        using var project = new TempProject(("milligram.json", """{ "prefix": "Shop", "levels": [["Domain"], ["Web"]] }"""), ("A.cs", Source));
        var workspace = Open(project);

        var model = workspace.Generate();

        Assert.True(File.Exists(workspace.Paths.ModelFile));
        Assert.Equal(2, model.Types.Count);
        var view = workspace.View(null, null);
        Assert.Equal(["ns:Domain", "ns:Web"], view.Nodes.Where(n => n.Parent is null).Select(n => n.Id));
        Assert.Contains(view.Edges, e => e.From == "t:Shop.Web.Page" && e.To == "t:Shop.Domain.Order");
        Assert.Equal(["A.cs"], workspace.FilesOf(null, "ns:Web"));
        Assert.Equal("Order", workspace.Card(null, "Shop.Domain.Order")!.Name);
    }

    [Fact]
    public void ABrokenPolicyKeepsTheLastGoodOne()
    {
        using var project = new TempProject(("milligram.json", """{ "prefix": "Shop" }"""));
        var workspace = Open(project);
        File.WriteAllText(workspace.Paths.PolicyFile, "{ nope");

        Assert.False(workspace.ReloadPolicy());
        Assert.Equal("Shop", workspace.Policy.Prefix);
        Assert.NotNull(workspace.PolicyError);
    }

    [Fact]
    public void PolicyEditsAreWrittenAndVisible()
    {
        using var project = new TempProject(("milligram.json", """{ "prefix": "Shop" }"""), ("A.cs", Source));
        var workspace = Open(project);
        workspace.Generate();
        var editor = new PolicyEditor(workspace);

        var proposal = editor.NewProposal("Split");
        editor.Omit("Web", proposal.Id);
        editor.Omit("Domain", null);
        editor.RenameProposal(proposal.Id, "Split v2");

        var reread = JsonFile.Read<Policy>(workspace.Paths.PolicyFile)!;
        Assert.Equal("Split v2", reread.Proposals.Single().Name);
        Assert.Equal(["Web"], reread.Proposals[0].Omit);
        Assert.Equal(["Domain"], reread.Omit);
        Assert.True(workspace.Tree(proposal.Id).IsProposal);
        Assert.False(workspace.Tree(DiagramTree.RealContext).Contains("Shop.Domain.Order"));

        editor.DeleteProposal(proposal.Id);
        Assert.Empty(workspace.Policy.Proposals);
    }
}

public class ProjectInitializerTests
{
    [Theory]
    [InlineData(new[] { "Shop.Domain", "Shop.Web.Pages" }, "Shop")]
    [InlineData(new[] { "Shop.Domain.Model", "Shop.Domain.Rules" }, "Shop.Domain")]
    [InlineData(new[] { "Shop", "Other" }, "")]
    [InlineData(new string[0], "")]
    public void CommonPrefixIsTheSharedLeadingSegments(string[] namespaces, string expected) =>
        Assert.Equal(expected, ProjectInitializer.CommonPrefix(namespaces));

    [Fact]
    public void InitializeWritesAPolicyFromTheSourceAndIgnoresRunFiles()
    {
        using var project = new TempProject(
            ("src/Shop/Domain/Order.cs", "namespace Shop.Domain; public class Order { }"),
            ("src/Shop/Web/Page.cs", "namespace Shop.Web; public class Page { }"),
            ("tests/Shop.Tests/Shop.Tests.csproj", "<Project><ItemGroup><PackageReference Include=\"xunit\" /></ItemGroup></Project>"),
            ("tests/Shop.Tests/T.cs", "namespace Shop.Tests; public class T { }"));
        var paths = new ProjectPaths(project.Root);
        var initializer = new ProjectInitializer(paths, new CSharpScanner(), new Milligram.Analysis.DotNet.DotNetProjectLocator());

        Assert.True(initializer.Initialize(force: false));
        Assert.False(initializer.Initialize(force: false));

        var policy = JsonFile.Read<Policy>(paths.PolicyFile)!;
        Assert.Equal("src", policy.Src);
        Assert.Equal("Shop", policy.Prefix);
        Assert.Equal(["Domain", "Web"], policy.Order);
        Assert.Contains("tests/Shop.Tests/**", policy.Exclude);
        Assert.Contains(".milligram/run/", File.ReadAllLines(Path.Combine(project.Root, ".gitignore")));
    }
}

public class ProjectPathsTests
{
    [Fact]
    public void ContainsRejectsTraversal()
    {
        var paths = new ProjectPaths("/tmp/project");
        Assert.True(paths.Contains("/tmp/project/src/A.cs"));
        Assert.True(paths.Contains("src/A.cs"));
        Assert.False(paths.Contains("../other/A.cs"));
        Assert.False(paths.Contains("/tmp/project-evil/A.cs"));
        Assert.False(paths.Contains("/etc/passwd"));
    }
}

public class CommandLineTests
{
    [Fact]
    public void DefaultsToServe() => Assert.Equal("serve", CommandLine.Parse([]).Command);

    [Fact]
    public void ParsesCommandsFlagsOptionsAndArguments()
    {
        var line = CommandLine.Parse(["mutate", "--all", "a.cs", "--port", "6000", "b.cs", "--coverage=x.xml", "--coverage", "y.xml"]);
        Assert.Equal("mutate", line.Command);
        Assert.True(line.Has("all"));
        Assert.Equal(["a.cs", "b.cs"], line.Arguments);
        Assert.Equal(6000, line.IntValue("port", 1));
        Assert.Equal(["x.xml", "y.xml"], line.Values("coverage"));
        Assert.Equal(7, line.IntValue("missing", 7));
    }
}
