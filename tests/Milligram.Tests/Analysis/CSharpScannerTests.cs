using Microsoft.CodeAnalysis.CSharp;
using Milligram.Analysis.CSharp;
using Milligram.Application;
using Milligram.Domain.Model;

namespace Milligram.Tests.Analysis;

public class CSharpScannerTests
{
    private const string Domain = """
        namespace Shop.Domain;

        public interface IPriced { decimal Price { get; } }

        public abstract class Item : IPriced
        {
            public abstract decimal Price { get; }
        }

        public sealed record Book(string Isbn, decimal Cost) : Item
        {
            public override decimal Price => Cost > 100 ? Cost * 0.9m : Cost;

            private sealed class Cache
            {
                public Book? Last;
            }
        }

        public enum Color { Red, Green = 2 }
        """;

    private const string Services = """
        using System.Text.Json;
        using Shop.Domain;

        namespace Shop.Services
        {
            public static class Pricing
            {
                public static decimal Total(IEnumerable<IPriced> items, bool vip)
                {
                    var total = 0m;
                    foreach (var item in items)
                        if (vip && item.Price > 10 || item is Book { Cost: > 5 })
                            total += item.Price;
                    return total;
                }

                public static string Save(Book book) => JsonSerializer.Serialize(book);
            }

            public partial class Cart
            {
                public int Count { get; set; }
            }

            public partial class Cart
            {
                public Color Tint() => Color.Red;
            }
        }
        """;

    private static CodeModel Scan(TempProject project, params string[] foreign) =>
        new CSharpScanner().Scan(new ScanRequest(project.Root, project.Root, ["**/bin/**", "**/obj/**"], "Shop", foreign, "Shop"));

    [Fact]
    public void FindsTopLevelTypesWithKindsAndNamespaces()
    {
        using var project = new TempProject(("Domain.cs", Domain), ("Services.cs", Services));
        var model = Scan(project);

        var ids = model.Types.Select(t => t.Id).ToList();
        Assert.Equal(["Shop.Domain.Book", "Shop.Domain.Color", "Shop.Domain.IPriced", "Shop.Domain.Item", "Shop.Services.Cart", "Shop.Services.Pricing"], ids);
        Assert.Equal(TypeKind.Record, model.Types.Single(t => t.Name == "Book").Kind);
        Assert.Equal(TypeKind.Interface, model.Types.Single(t => t.Name == "IPriced").Kind);
        Assert.True(model.Types.Single(t => t.Name == "Item").IsAbstract);
        Assert.True(model.Types.Single(t => t.Name == "Pricing").IsStatic);
        Assert.Equal("Shop.Services", model.Types.Single(t => t.Name == "Cart").Namespace);
    }

    [Fact]
    public void NestedTypesFoldIntoTheirOuterType()
    {
        using var project = new TempProject(("Domain.cs", Domain));
        var book = Scan(project).Types.Single(t => t.Name == "Book");
        Assert.Contains(book.Members, m => m.Name == "Cache.Last" && m.Signature == "Cache.Last: Book?" && m.Kind == MemberKind.Field);
    }

    [Fact]
    public void PartialTypesMergeTheirDeclarations()
    {
        using var project = new TempProject(("Services.cs", Services));
        var cart = Scan(project).Types.Single(t => t.Name == "Cart");
        Assert.Equal(2, cart.Spans.Count);
        Assert.Equal(["Count", "Tint"], cart.Members.Select(m => m.Name).Order());
    }

    [Fact]
    public void MembersHaveIdsSignaturesVisibilityAndComplexity()
    {
        using var project = new TempProject(("Domain.cs", Domain), ("Services.cs", Services));
        var pricing = Scan(project).Types.Single(t => t.Name == "Pricing");

        var total = pricing.Members.Single(m => m.Name == "Total");
        Assert.Equal("Shop.Services.Pricing.Total(IEnumerable<IPriced>,bool)", total.Id);
        Assert.Equal("Total(IEnumerable<IPriced> items, bool vip): decimal", total.Signature);
        Assert.Equal(Visibility.Public, total.Visibility);
        Assert.True(total.IsStatic);
        Assert.Equal(5, total.Complexity);
        Assert.Equal("Services.cs", total.Span.File);
        Assert.Equal(8, total.Span.StartLine);
        Assert.Equal(1, pricing.Members.Single(m => m.Name == "Save").Complexity);
    }

    [Fact]
    public void AutoPropertiesAndFieldsHaveNoComplexity()
    {
        using var project = new TempProject(("Domain.cs", Domain), ("Services.cs", Services));
        var model = Scan(project);
        Assert.Null(model.Types.Single(t => t.Name == "Cart").Members.Single(m => m.Name == "Count").Complexity);
        Assert.Equal(2, model.Types.Single(t => t.Name == "Book").Members.Single(m => m.Name == "Price").Complexity);
        Assert.Equal(["Green = 2", "Red"], model.Types.Single(t => t.Name == "Color").Members.Select(m => m.Signature).Order());
    }

    [Fact]
    public void RecordPrimaryConstructorsAreMembers()
    {
        using var project = new TempProject(("Domain.cs", Domain));
        var book = Scan(project).Types.Single(t => t.Name == "Book");
        var ctor = book.Members.Single(m => m.Kind == MemberKind.Constructor);
        Assert.Equal("Book(string Isbn, decimal Cost)", ctor.Signature);
    }

    [Fact]
    public void EdgesCarryInheritanceImplementationAndUse()
    {
        using var project = new TempProject(("Domain.cs", Domain), ("Services.cs", Services));
        var edges = Scan(project).Edges;

        EdgeKind KindOf(string from, string to) => edges.Single(e => e.From == from && e.To == to).Kind;
        Assert.Equal(EdgeKind.Inheritance, KindOf("Shop.Domain.Book", "Shop.Domain.Item"));
        Assert.Equal(EdgeKind.Implements, KindOf("Shop.Domain.Item", "Shop.Domain.IPriced"));
        Assert.Equal(EdgeKind.Dependency, KindOf("Shop.Services.Pricing", "Shop.Domain.Book"));
        Assert.Equal(EdgeKind.Dependency, KindOf("Shop.Services.Cart", "Shop.Domain.Color"));
        Assert.DoesNotContain(edges, e => e.From == e.To);
    }

    [Fact]
    public void ListedForeignLibrariesBecomeNodes()
    {
        using var project = new TempProject(("Domain.cs", Domain), ("Services.cs", Services));
        var model = Scan(project, "System.Text.Json");

        var json = Assert.Single(model.Foreign);
        Assert.Equal("System.Text.Json", json.Label);
        Assert.Contains(model.Edges, e => e.From == "Shop.Services.Pricing" && e.To == json.Id);
    }

    [Fact]
    public void UnlistedLibrariesAreIgnored()
    {
        using var project = new TempProject(("Services.cs", Services), ("Domain.cs", Domain));
        var model = Scan(project);
        Assert.Empty(model.Foreign);
        Assert.All(model.Edges, e => Assert.StartsWith("Shop.", e.To));
    }

    [Fact]
    public void TopLevelStatementsBecomeAProgramType()
    {
        using var project = new TempProject(("Program.cs", """
            using Shop.Domain;
            var color = args.Length > 0 ? Color.Red : Color.Green;
            System.Console.WriteLine(color);
            """), ("Domain.cs", Domain));
        var program = Scan(project).Types.Single(t => t.Name == "Program");

        Assert.Equal("", program.Namespace);
        var main = Assert.Single(program.Members);
        Assert.Equal(MemberKind.TopLevel, main.Kind);
        Assert.Equal(2, main.Complexity);
    }

    [Fact]
    public void ExcludedFilesAreNotScanned()
    {
        using var project = new TempProject(("src/Domain.cs", Domain), ("src/bin/Generated.cs", "namespace Shop.Gen; class G {}"), ("tests/T.cs", "namespace Shop.Tests; class T {}"));
        var model = new CSharpScanner().Scan(new ScanRequest(project.Root, project.Root, ["**/bin/**", "tests/**"], "Shop", [], "Shop"));
        Assert.DoesNotContain(model.Types, t => t.Namespace is "Shop.Gen" or "Shop.Tests");
    }

    [Fact]
    public void HashesChangeWhenTheBodyChanges()
    {
        using var first = new TempProject(("A.cs", "namespace Shop; class A { int M() => 1; }"));
        using var second = new TempProject(("A.cs", "namespace Shop; class A { int M() => 2; }"));
        Assert.NotEqual(Scan(first).Types[0].Members[0].Hash, Scan(second).Types[0].Members[0].Hash);
    }
}

public class ComplexityTests
{
    [Theory]
    [InlineData("void M() { }", 1)]
    [InlineData("void M(bool a) { if (a) { } else { } }", 2)]
    [InlineData("int M(int x) => x switch { 1 => 1, 2 => 2, _ => 0 };", 3)]
    [InlineData("int M(int? x) => x ?? 0;", 2)]
    [InlineData("void M(bool a, bool b) { while (a && b) { } }", 3)]
    [InlineData("void M() { try { } catch (System.Exception) { } }", 2)]
    [InlineData("string? M(object? o) => o?.ToString();", 2)]
    [InlineData("void M(int x) { switch (x) { case 1: break; case 2: break; default: break; } }", 3)]
    [InlineData("bool M(int x) => x is 1 or 2;", 2)]
    [InlineData("void M(int[] xs) { foreach (var x in xs) { System.Func<int, int> f = y => y > 0 ? y : -y; } }", 3)]
    public void CountsBranchPoints(string member, int expected)
    {
        var tree = CSharpSyntaxTree.ParseText($"class C {{ {member} }}");
        var method = tree.GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MemberDeclarationSyntax>().Last();
        Assert.Equal(expected, Complexity.Of(method));
    }
}
