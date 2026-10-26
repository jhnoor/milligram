using Milligram.Analysis.CSharp;
using Milligram.Application;
using Milligram.Domain.Boards;

namespace Milligram.Tests.Application;

public class BoardStoreTests : IDisposable
{
    private const string Flow = """
        {
          // the agent may comment
          "title": "How an order is priced",
          "boxes": [{ "id": "a", "ref": "Shop.Domain.Order" }, { "id": "u", "label": "the customer" },],
          "arrows": [{ "from": "u", "to": "a", "label": "orders" }]
        }
        """;

    private readonly TempProject project = new();
    private readonly ProjectPaths paths;
    private readonly BoardStore store;

    public BoardStoreTests()
    {
        paths = new ProjectPaths(project.Root);
        store = new BoardStore(paths);
    }

    public void Dispose() => project.Dispose();

    private string Draft(string id) => Path.Combine(paths.BoardDraftsDirectory, id + ".json");
    private string Kept(string id) => Path.Combine(paths.BoardsDirectory, id + ".json");

    [Fact]
    public void DrawingThePenStoresADraftUnderItsTitlesSlug()
    {
        project.Write(".milligram/board.json", Flow);

        var board = store.Draw();

        Assert.Equal("how-an-order-is-priced", board.Id);
        var saved = JsonFile.Read<Board>(Draft("how-an-order-is-priced"))!;
        Assert.Equal("how-an-order-is-priced", saved.Id);
        Assert.Equal(2, saved.Boxes.Count);
        Assert.Equal("orders", saved.Arrows[0].Label);
        Assert.False(File.Exists(Kept("how-an-order-is-priced")));
    }

    [Fact]
    public void AnotherFileCanBeDrawnUnderItsOwnId()
    {
        var file = project.Write("elsewhere/flow.json", Flow.Replace("\"title\"", "\"id\": \"v2\", \"title\"", StringComparison.Ordinal));

        Assert.Equal("v2", store.Draw(file).Id);
        Assert.True(File.Exists(Draft("v2")));
    }

    [Fact]
    public void WithoutAPenTheAgentIsToldWhereToWrite()
    {
        var error = Assert.Throws<MilligramException>(() => store.Draw());

        Assert.Equal("Write the board to .milligram/board.json first, then run `milligram tell board`.", error.Message);
    }

    [Fact]
    public void BrokenJsonNamesTheFile()
    {
        project.Write(".milligram/board.json", "{ \"title\": ");

        var error = Assert.Throws<MilligramException>(() => store.Draw());

        Assert.StartsWith(".milligram/board.json: ", error.Message);
        Assert.False(Directory.Exists(paths.BoardDraftsDirectory));
    }

    [Fact]
    public void AFileOutsideTheProjectIsNamedInFull()
    {
        using var outside = new TempProject(("flow.json", "["));
        var file = Path.Combine(outside.Root, "flow.json");

        var error = Assert.Throws<MilligramException>(() => store.Draw(file));

        Assert.StartsWith(file + ": ", error.Message);
    }

    [Fact]
    public void EveryProblemIsReportedAndNothingIsStored()
    {
        project.Write(".milligram/board.json", """{ "title": "Flow", "boxes": [{ "id": "a" }], "arrows": [{ "from": "a", "to": "b" }] }""");

        var error = Assert.Throws<MilligramException>(() => store.Draw());

        Assert.Equal(
            ".milligram/board.json can't be drawn:\n- Box \"a\" needs a ref, a label or a note.\n- Arrow 1 points at \"b\", which is not a box id.",
            error.Message);
        Assert.Empty(store.List());
    }

    [Fact]
    public void RedrawingAnIdReplacesItsDraft()
    {
        project.Write(".milligram/board.json", """{ "id": "flow", "title": "First", "boxes": [{ "id": "a", "label": "A" }] }""");
        store.Draw();
        project.Write(".milligram/board.json", """{ "id": "flow", "title": "Second", "boxes": [{ "id": "a", "label": "A" }] }""");
        store.Draw();

        Assert.Equal("Second", store.Load("flow")!.Title);
        Assert.Equal([new BoardInfo("flow", "Second", false, true)], store.List());
    }

    [Fact]
    public void KeepingMovesTheDraftOverTheKeptBoard()
    {
        project.Write(".milligram/boards/flow.json", """{ "title": "Old", "boxes": [{ "id": "a", "label": "A" }] }""");
        project.Write(".milligram/boards/drafts/flow.json", """{ "title": "New", "boxes": [{ "id": "a", "label": "A" }] }""");

        Assert.Equal("New", store.Load("flow")!.Title);
        Assert.True(store.Keep("flow"));

        Assert.False(File.Exists(Draft("flow")));
        Assert.Equal("New", store.Load("flow")!.Title);
        Assert.Equal([new BoardInfo("flow", "New", true, false)], store.List());
        Assert.False(store.Keep("flow"));
    }

    [Fact]
    public void DiscardingRemovesTheDraftAndTheKeptBoard()
    {
        project.Write(".milligram/boards/flow.json", """{ "title": "Old", "boxes": [] }""");
        project.Write(".milligram/boards/drafts/flow.json", """{ "title": "New", "boxes": [] }""");

        Assert.True(store.Discard("flow"));

        Assert.False(File.Exists(Draft("flow")));
        Assert.False(File.Exists(Kept("flow")));
        Assert.False(store.Discard("flow"));
    }

    [Fact]
    public void TheListMergesKeptBoardsAndDraftsInIdOrder()
    {
        project.Write(".milligram/boards/zeta.json", """{ "title": "Zeta" }""");
        project.Write(".milligram/boards/alpha.json", """{ "title": "" }""");
        project.Write(".milligram/boards/drafts/alpha.json", """{ "title": "Alpha" }""");
        project.Write(".milligram/boards/drafts/mid.json", """{ "title": "Mid" }""");
        project.Write(".milligram/boards/Bad Name.json", """{ "title": "Ignored" }""");
        project.Write(".milligram/boards/broken.json", "{ nope");
        project.Write(".milligram/boards/notes.txt", "not a board");
        project.Write(".milligram/boards/drafts/zeta.txt", "not a draft of zeta");

        Assert.Equal(
        [
            new BoardInfo("alpha", "Alpha", true, true),
            new BoardInfo("mid", "Mid", false, true),
            new BoardInfo("zeta", "Zeta", true, false),
        ], store.List());
    }

    [Fact]
    public void AnUntitledBoardIsListedByItsId()
    {
        project.Write(".milligram/boards/plain.json", """{ "boxes": [] }""");

        Assert.Equal([new BoardInfo("plain", "plain", true, false)], store.List());
    }

    [Fact]
    public void NoBoardsMeansAnEmptyList() => Assert.Empty(store.List());

    [Theory]
    [InlineData("../milligram")]
    [InlineData("..")]
    [InlineData("Flow")]
    [InlineData("")]
    public void OnlyBoardIdsReachTheFileSystem(string id)
    {
        project.Write(".milligram/milligram.json", "{}");
        project.Write(".milligram/boards/drafts/x.json", """{ "title": "X" }""");

        Assert.Null(store.Load(id));
        Assert.False(store.Keep(id));
        Assert.False(store.Discard(id));
        Assert.True(File.Exists(paths.PolicyFile));
    }

    [Fact]
    public void AnUnknownBoardLoadsAsNothing() => Assert.Null(store.Load("nothing"));

    [Fact]
    public void ABrokenBoardFileIsReported()
    {
        project.Write(".milligram/boards/flow.json", "{ nope");

        var error = Assert.Throws<MilligramException>(() => store.Load("flow"));

        Assert.StartsWith(".milligram/boards/flow.json: ", error.Message);
    }

    [Fact]
    public void ANewViewerStartsWithoutDrafts()
    {
        project.Write(".milligram/boards/kept.json", """{ "title": "Kept" }""");
        project.Write(".milligram/boards/drafts/a.json", """{ "title": "A" }""");
        project.Write(".milligram/boards/drafts/b.json", """{ "title": "B" }""");
        var note = project.Write(".milligram/boards/drafts/notes.txt", "not a board");

        store.ClearDrafts();

        Assert.Equal([new BoardInfo("kept", "Kept", true, false)], store.List());
        Assert.True(File.Exists(note));
        new BoardStore(new ProjectPaths(Path.Combine(project.Root, "nowhere"))).ClearDrafts();
    }
}

public class WorkspaceBoardTests
{
    [Fact]
    public void ABoardContextDrawsTheBoardAgainstTheCurrentModel()
    {
        using var project = new TempProject(
            (".milligram/milligram.json", """{ "prefix": "Shop" }"""),
            ("Order.cs", "namespace Shop.Domain { public class Order { public int Total() => 1; } }"),
            (".milligram/boards/flow.json", """{ "title": "Flow", "boxes": [{ "id": "o", "ref": "Domain.Order.Total" }, { "id": "x", "ref": "Domain.Gone" }] }"""));
        var workspace = new Workspace(new ProjectPaths(project.Root), new CSharpScanner());
        workspace.Load();
        workspace.Generate();

        var view = workspace.View("board:flow", null);

        Assert.True(view.Context.IsBoard);
        Assert.Equal(["Order.Total", "Domain.Gone"], view.Nodes.Select(n => n.Label));
        Assert.Equal("Shop.Domain.Order", view.Nodes[0].TypeId);
    }

    [Theory]
    [InlineData("board:missing")]
    [InlineData("board:broken")]
    [InlineData("board:../milligram")]
    public void AnUnknownOrUnreadableBoardFallsBackToTheRealDiagram(string context)
    {
        using var project = new TempProject(
            (".milligram/milligram.json", """{ "prefix": "Shop" }"""),
            ("Order.cs", "namespace Shop.Domain { public class Order { } }"),
            (".milligram/boards/broken.json", "{ nope"));
        var workspace = new Workspace(new ProjectPaths(project.Root), new CSharpScanner());
        workspace.Load();
        workspace.Generate();

        var view = workspace.View(context, null);

        Assert.False(view.Context.IsBoard);
        Assert.Equal("real", view.Context.Id);
    }
}
