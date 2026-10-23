using Milligram.Domain.Boards;
using Milligram.Domain.Metrics;
using Milligram.Domain.Model;
using Milligram.Domain.Policies;
using Milligram.Domain.Views;

namespace Milligram.Tests.Domain;

public class BoardTests
{
    [Theory]
    [InlineData("order-pricing", true)]
    [InlineData("a", true)]
    [InlineData("v2-flow-3", true)]
    [InlineData("z09", true)]
    [InlineData("", false)]
    [InlineData("Order", false)]
    [InlineData("-order", false)]
    [InlineData("order-", false)]
    [InlineData("../milligram", false)]
    [InlineData("order pricing", false)]
    [InlineData("order_pricing", false)]
    public void IdsAreLowercaseLettersDigitsAndInnerHyphens(string id, bool valid) =>
        Assert.Equal(valid, Board.IsValidId(id));

    [Fact]
    public void IdsHaveAtMostSixtyFourCharacters()
    {
        Assert.True(Board.IsValidId(new string('a', 64)));
        Assert.False(Board.IsValidId(new string('a', 65)));
    }

    [Theory]
    [InlineData("How an order is priced", "how-an-order-is-priced")]
    [InlineData("  C# → Python!! ", "c-python")]
    [InlineData("v2: the flow", "v2-the-flow")]
    [InlineData("Z09 / 0z9", "z09-0z9")]
    [InlineData("→ ←", "")]
    public void TitlesBecomeSlugs(string title, string slug) => Assert.Equal(slug, Board.Slug(title));

    [Fact]
    public void ALongTitleBecomesAValidId()
    {
        var slug = Board.Slug(string.Join(' ', Enumerable.Repeat("word", 30)));

        Assert.True(Board.IsValidId(slug), slug);
        Assert.True(slug.Length <= 64);
    }

    [Fact]
    public void TheBoardsOwnIdWinsOverItsTitle()
    {
        Assert.Equal("mine", new Board { Id = "mine", Title = "Something else" }.IdOrSlug());
        Assert.Equal("something-else", new Board { Title = "Something else" }.IdOrSlug());
    }

    [Fact]
    public void AWellFormedBoardHasNoProblems()
    {
        var board = new Board
        {
            Title = "Flow",
            Boxes = [new() { Id = "a", Ref = "App.A" }, new() { Id = "b", Label = "the user" }, new() { Id = "n", Note = "Why." }, new() { Id = "c", Ref = "App.C", Label = "C" }],
            Arrows = [new() { From = "b", To = "a", Label = "asks" }, new() { From = "a", To = "c" }],
        };

        Assert.Empty(board.Problems());
    }

    [Fact]
    public void EveryProblemIsListedInTheOrderToFixIt()
    {
        var board = new Board
        {
            Boxes = [new() { Ref = "App.A" }, new() { Id = "b", Label = "x" }, new() { Id = "b", Label = "y" }, new() { Id = "c" }, new() { Id = "d", Ref = "App.D", Note = "and a note" }],
            Arrows = [new() { From = "b", To = "zz" }, new() { To = "b" }],
        };

        Assert.Equal(
        [
            "Give the board a title or an id.",
            "Box 1 needs an id.",
            "Box \"b\" appears more than once.",
            "Box \"c\" needs a ref, a label or a note.",
            "Box \"d\" is either code (ref) or a note, not both.",
            "Arrow 1 points at \"zz\", which is not a box id.",
            "Arrow 2 points at \"\", which is not a box id.",
        ], board.Problems());
    }

    [Fact]
    public void AnEmptyIdCountsAsNoId()
    {
        var problems = new Board { Id = "", Boxes = [new() { Id = "", Label = "x" }] }.Problems();

        Assert.Equal(["Give the board a title or an id.", "Box 1 needs an id."], problems);
    }

    [Fact]
    public void AnInvalidIdAndAnEmptyBoardAreProblems()
    {
        Assert.Equal(
            ["id \"Bad Id\" must be 1-64 lowercase letters, digits or inner hyphens.", "A board needs at least one box."],
            new Board { Id = "Bad Id" }.Problems());
    }

    [Fact]
    public void BoardsAreKeptSmallEnoughToRead()
    {
        var boxes = Enumerable.Range(0, Board.MaxBoxes + 1).Select(i => new BoardBox { Id = $"b{i}", Label = "x" }).ToList();
        var arrows = Enumerable.Range(0, Board.MaxArrows + 1).Select(_ => new BoardArrow { From = "b0", To = "b1" }).ToList();

        Assert.Equal(
            [$"A board can have at most {Board.MaxBoxes} boxes.", $"A board can have at most {Board.MaxArrows} arrows."],
            new Board { Title = "Big", Boxes = boxes, Arrows = arrows }.Problems());
        Assert.Empty(new Board { Title = "Fits", Boxes = boxes[..Board.MaxBoxes], Arrows = arrows[..Board.MaxArrows] }.Problems());
    }
}

public class BoardViewBuilderTests
{
    private static readonly MemberNode Run = Build.Member("App.Orders.Handler", "Run", complexity: 4, file: "Orders/Handler.cs", startLine: 12, endLine: 20, hash: "run");
    private static readonly MemberNode Undo = Build.Member("App.Orders.Handler", "Undo", complexity: 1, file: "Orders/Handler.cs", startLine: 30, endLine: 31, hash: "undo");
    private static readonly CodeModel Model = Build.Model([Build.Type("App.Orders.Handler", Run, Undo), Build.Type("App.Pricing.Quote", TypeKind.Record)]);

    private static DiagramView View(params BoardBox[] boxes) =>
        BoardViewBuilder.Build(new Board { Title = "Flow", Boxes = boxes }, Model, Build.Policy(), MetricsSet.Empty);

    [Fact]
    public void ABoardIsItsOwnContextWithOneBreadcrumb()
    {
        var view = BoardViewBuilder.Build(
            new Board { Id = "flow", Title = "How it flows", Boxes = [new() { Id = "a", Label = "A" }] }, Model, Build.Policy(), MetricsSet.Empty);

        Assert.Equal(new ContextInfo("board:flow", "How it flows", false) { IsBoard = true }, view.Context);
        Assert.Equal("How it flows", view.Title);
        Assert.Equal(new NodeRef("board:flow", "How it flows"), Assert.Single(view.Breadcrumbs));
        Assert.Null(view.MaxLevel);
    }

    [Fact]
    public void AnUntitledBoardIsNamedByItsId()
    {
        var view = BoardViewBuilder.Build(new Board { Id = "flow", Boxes = [new() { Id = "a", Label = "A" }] }, Model, Build.Policy(), MetricsSet.Empty);

        Assert.Equal("flow", view.Title);
        Assert.Equal("flow", view.Context.Name);
    }

    [Fact]
    public void BoardContextsAreRecognisedByTheirPrefix()
    {
        Assert.Equal("board:flow", BoardViewBuilder.ContextOf("flow"));
        Assert.Equal("flow", BoardViewBuilder.BoardIdOf("board:flow"));
        Assert.Null(BoardViewBuilder.BoardIdOf("real"));
        Assert.Null(BoardViewBuilder.BoardIdOf(null));
        Assert.Null(BoardViewBuilder.BoardIdOf("p1"));
    }

    [Theory]
    [InlineData("App.Pricing.Quote")]
    [InlineData("Pricing.Quote")]
    [InlineData("t:App.Pricing.Quote")]
    [InlineData("  App.Pricing.Quote ")]
    public void ATypeReferenceBecomesThatTypesBox(string reference)
    {
        var node = Assert.Single(View(new BoardBox { Id = "q", Ref = reference }).Nodes);

        Assert.Equal("b:q", node.Id);
        Assert.Equal("Quote", node.Label);
        Assert.Equal(ViewNodeKind.Type, node.Kind);
        Assert.Equal("record", node.Stereotype);
        Assert.Equal("App.Pricing.Quote", node.TypeId);
        Assert.Equal("App.Pricing", node.Namespace);
        Assert.Equal("App.Pricing", node.Caption);
        Assert.Equal("Pricing.Quote", node.Target);
        Assert.Equal("Quote.cs", node.File);
        Assert.Equal(1, node.Line);
    }

    [Theory]
    [InlineData("App.Orders.Handler.Undo()")]
    [InlineData("App.Orders.Handler.Undo")]
    [InlineData("Orders.Handler.Undo")]
    [InlineData("Orders.Handler.Undo(int)")]
    public void AMemberReferenceBecomesThatMembersBox(string reference)
    {
        var node = Assert.Single(View(new BoardBox { Id = "u", Ref = reference }).Nodes);

        Assert.Equal("Handler.Undo", node.Label);
        Assert.Equal("method", node.Stereotype);
        Assert.Equal("App.Orders.Handler", node.TypeId);
        Assert.Equal("Orders/Handler.cs", node.File);
        Assert.Equal(30, node.Line);
    }

    [Fact]
    public void AnExactMemberIdPicksThatOverload()
    {
        var first = Build.Member("App.Calc", "Add", startLine: 5) with { Id = "App.Calc.Add(int)" };
        var second = Build.Member("App.Calc", "Add", startLine: 9) with { Id = "App.Calc.Add(string)" };
        var model = Build.Model([Build.Type("App.Calc", first, second)]);

        var view = BoardViewBuilder.Build(new Board { Title = "Flow", Boxes = [new() { Id = "a", Ref = "App.Calc.Add(string)" }, new() { Id = "b", Ref = "Calc.Add" }] },
            model, Build.Policy(), MetricsSet.Empty);

        Assert.Equal([9, 5], view.Nodes.Select(n => n.Line));
    }

    [Fact]
    public void ThePrefixIsAddedOnlyWhereItIsMissingAsAWholeSegment()
    {
        var model = Build.Model([Build.Type("App.Apple.Pie"), Build.Type("App.App.Box")]);

        var view = BoardViewBuilder.Build(new Board { Title = "Flow", Boxes = [new() { Id = "a", Ref = "Apple.Pie" }, new() { Id = "b", Ref = "App.Box" }] },
            model, Build.Policy(), MetricsSet.Empty);

        Assert.Equal(["App.Apple.Pie", "App.App.Box"], view.Nodes.Select(n => n.TypeId));
    }

    [Fact]
    public void ATypeWithoutSourceHasNoSourceLink()
    {
        var model = Build.Model([Build.Type("App.Generated") with { Spans = [] }]);

        var node = Assert.Single(BoardViewBuilder.Build(new Board { Title = "Flow", Boxes = [new() { Id = "g", Ref = "App.Generated" }] }, model, Build.Policy(), MetricsSet.Empty).Nodes);

        Assert.Equal("App.Generated", node.TypeId);
        Assert.Null(node.File);
        Assert.Null(node.Line);
    }

    [Fact]
    public void ALabelRenamesACodeBoxWithoutLosingItsCode()
    {
        var node = Assert.Single(View(new BoardBox { Id = "r", Ref = "App.Orders.Handler.Run", Label = "Run()" }).Nodes);

        Assert.Equal("Run()", node.Label);
        Assert.Equal("App.Orders.Handler", node.TypeId);
    }

    [Theory]
    [InlineData("App.Orders.Gone")]
    [InlineData("App.Orders.Handler.Gone")]
    [InlineData("Handler")]
    [InlineData(".Run")]
    public void AReferenceToNoCodeIsDrawnAsMissing(string reference)
    {
        var node = Assert.Single(View(new BoardBox { Id = "m", Ref = reference }).Nodes);

        Assert.Equal(ViewNodeKind.Missing, node.Kind);
        Assert.Equal(reference, node.Label);
        Assert.Equal($"not in the code: {reference}", node.Caption);
        Assert.Null(node.TypeId);
        Assert.Null(node.File);
    }

    [Fact]
    public void AMissingBoxKeepsItsLabel()
    {
        var node = Assert.Single(View(new BoardBox { Id = "m", Ref = "App.Gone", Label = "Old pricing" }).Nodes);

        Assert.Equal("Old pricing", node.Label);
        Assert.Equal(ViewNodeKind.Missing, node.Kind);
    }

    [Fact]
    public void LabelsAndNotesStandForWhatHasNoCode()
    {
        var nodes = View(new BoardBox { Id = "u", Label = " the customer " }, new BoardBox { Id = "n", Note = "Prices come from Pricing." }).Nodes;

        Assert.Equal(new ViewNode { Id = "b:u", Label = "the customer", Kind = ViewNodeKind.Free }, nodes[0]);
        Assert.Equal(new ViewNode { Id = "b:n", Label = "Prices come from Pricing.", Kind = ViewNodeKind.Note }, nodes[1]);
    }

    [Fact]
    public void ArrowsJoinBoxesAndKeepTheirLabels()
    {
        var view = BoardViewBuilder.Build(new Board
        {
            Title = "Flow",
            Boxes = [new() { Id = "a", Label = "A" }, new() { Id = "b", Label = "B" }],
            Arrows = [new() { From = "a", To = "b", Label = " asks " }, new() { From = "b", To = "a", Label = " " }],
        }, Model, Build.Policy(), MetricsSet.Empty);

        Assert.Equal(["b:a", "b:b"], view.Nodes.Select(n => n.Id));
        Assert.Equal("asks", view.Edges[0].Label);
        Assert.Null(view.Edges[1].Label);
        Assert.All(view.Edges, e => Assert.False(e.Violating));
        Assert.Equal(("b:b", "b:a"), (view.Edges[1].From, view.Edges[1].To));
    }

    [Fact]
    public void CodeBoxesTakeTheGradesOfWhatTheyPointAt()
    {
        var crap = new CrapSnapshot(DateTimeOffset.UnixEpoch, new Dictionary<string, CrapEntry>
        {
            [Run.Id] = new(4, 0, 50, 10, 0, "run"),
            [Undo.Id] = new(1, 1, 1, 2, 2, "undo"),
        });
        var view = BoardViewBuilder.Build(new Board
        {
            Title = "Flow",
            Boxes = [new() { Id = "t", Ref = "App.Orders.Handler" }, new() { Id = "u", Ref = "App.Orders.Handler.Undo" }],
        }, Model, Build.Policy(), new MetricsSet(crap, MutationSnapshot.Empty));

        Assert.Equal(Grading.ForType(Model.Types[0], new MetricsSet(crap, MutationSnapshot.Empty), Build.Policy().Thresholds), view.Nodes[0].Grades);
        Assert.Equal(new Grades(10, null), view.Nodes[1].Grades);
    }

    [Fact]
    public void UnresolvedReferencesAreListedInBoardOrder()
    {
        var board = new Board
        {
            Title = "Flow",
            Boxes = [new() { Id = "a", Ref = " App.Gone " }, new() { Id = "b", Ref = "App.Pricing.Quote" }, new() { Id = "c", Label = "x" }, new() { Id = "d", Ref = "Orders.Nope" }],
        };

        Assert.Equal(["App.Gone", "Orders.Nope"], BoardViewBuilder.Unresolved(board, Model, "App"));
        Assert.Equal(["App.Gone", "Pricing.Quote", "Orders.Nope"],
            BoardViewBuilder.Unresolved(board with { Boxes = [.. board.Boxes.Take(1), new() { Id = "b", Ref = "Pricing.Quote" }, .. board.Boxes.Skip(2)] }, Model, ""));
    }
}

public class MemberGradingTests
{
    private static readonly MemberNode Member = Build.Member("App.A", "Run", complexity: 3, file: "A.cs", hash: "h1");
    private static readonly Thresholds Thresholds = new();

    [Fact]
    public void AMemberWithoutMeasurementsIsUnknown() =>
        Assert.Equal(Grades.Unknown, Grading.ForMember(Member, MetricsSet.Empty, Thresholds));

    [Fact]
    public void AMembersOwnCrapAndMutationScoresGradeIt()
    {
        var metrics = new MetricsSet(
            new CrapSnapshot(DateTimeOffset.UnixEpoch, new Dictionary<string, CrapEntry> { [Member.Id] = new(3, 0, Thresholds.CrapBad, 4, 0, "h1") }),
            new MutationSnapshot(DateTimeOffset.UnixEpoch,
                new Dictionary<string, MutationEntry> { [Member.Id] = new(9, 0, 1, 0, "h1") },
                new Dictionary<string, DateTimeOffset> { ["A.cs"] = DateTimeOffset.UnixEpoch }));

        Assert.Equal(new Grades(1, Grading.Scale(0.9, Thresholds.MutationGood, Thresholds.MutationBad)), Grading.ForMember(Member, metrics, Thresholds));
    }

    [Fact]
    public void MutationCountsOnlyWhenTheMembersFileWasTested()
    {
        var metrics = new MetricsSet(CrapSnapshot.Empty, new MutationSnapshot(DateTimeOffset.UnixEpoch,
            new Dictionary<string, MutationEntry> { [Member.Id] = new(1, 0, 0, 0, "h1") },
            new Dictionary<string, DateTimeOffset> { ["B.cs"] = DateTimeOffset.UnixEpoch }));

        Assert.Equal(Grades.Unknown, Grading.ForMember(Member, metrics, Thresholds));
    }

    [Fact]
    public void ATestedMemberWithoutMutationSitesGradesBest()
    {
        var metrics = new MetricsSet(CrapSnapshot.Empty, new MutationSnapshot(DateTimeOffset.UnixEpoch,
            new Dictionary<string, MutationEntry> { [Member.Id] = MutationEntry.None("h1") },
            new Dictionary<string, DateTimeOffset> { ["A.cs"] = DateTimeOffset.UnixEpoch }));

        Assert.Equal(new Grades(null, 10), Grading.ForMember(Member, metrics, Thresholds));
    }
}
