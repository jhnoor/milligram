using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Milligram.Analysis.CSharp;
using Milligram.Application;
using Milligram.Domain.Model;

namespace Milligram.Tests.Analysis;

public class ReferencesTests
{
    [Theory]
    [InlineData("<Project xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">")]
    [InlineData("<Project Sdk=\"Microsoft.NET.Sdk\">")]
    public void ExplicitAssemblyReferencesBindWithoutAProjectAssetsFile(string opening)
    {
        using var project = new TempProject(("App/App.csproj", $$"""
            {{opening}}<ItemGroup><Reference Include="Vendor">
              <HintPath>..\packages\Vendor.1.0\lib\Vendor.dll</HintPath>
            </Reference></ItemGroup></Project>
            """), ("App/Order.cs", "namespace Shop; public class Order : Vendor.Entity { }"));
        Emit(project, "packages/Vendor.1.0/lib/Vendor.dll", "namespace Vendor; public class Entity { }");

        var model = Scan(project);

        Assert.Contains(model.Edges, edge => edge.From == "Shop.Order" && edge.To == "x:Vendor" && edge.Kind == EdgeKind.Inheritance);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrInvalidAssembliesDoNotHideAValidReferenceWithTheSameFileName(bool invalid)
    {
        using var project = new TempProject(("App/App.csproj", """
            <Project><ItemGroup>
              <Reference Include="Broken"><HintPath>../broken/Vendor.dll</HintPath></Reference>
              <Reference Include="Vendor"><HintPath>../good/Vendor.dll</HintPath></Reference>
            </ItemGroup></Project>
            """), ("App/Order.cs", "namespace Shop; public class Order : Vendor.Entity { }"));
        if (invalid) project.Write("broken/Vendor.dll", "not an assembly");
        Emit(project, "good/Vendor.dll", "namespace Vendor; public class Entity { }");
        var log = new List<string>();

        Assert.Contains(Scan(project, log.Add).Edges, edge => edge.To == "x:Vendor");
        Assert.Contains(log, line => line.StartsWith("Reference unavailable:", StringComparison.Ordinal) && line.Contains("broken", StringComparison.Ordinal));
    }

    [Fact]
    public void ChangingTheHintPathChangesTheNextScanWithoutTouchingSource()
    {
        using var project = new TempProject(("App/App.csproj", "<Project />"),
            ("App/Order.cs", "namespace Shop; public class Order : Vendor.Entity { }"));
        Emit(project, "Vendor.dll", "namespace Vendor; public class Entity { }");
        Assert.Empty(Scan(project).Edges);

        project.Write("App/App.csproj", """
            <Project><ItemGroup><Reference Include="Vendor"><HintPath>../Vendor.dll</HintPath></Reference></ItemGroup></Project>
            """);

        Assert.Contains(Scan(project).Edges, edge => edge.To == "x:Vendor");
    }

    [Theory]
    [InlineData("<Project Condition=\"'$(Configuration)' == 'Debug'\"><ItemGroup><Reference Include=\"Vendor\"><HintPath>../Vendor.dll</HintPath></Reference></ItemGroup></Project>")]
    [InlineData("<Project><ItemGroup Condition=\"false\"><Reference Include=\"Vendor\"><HintPath>../Vendor.dll</HintPath></Reference></ItemGroup></Project>")]
    [InlineData("<Project><ItemGroup><Reference Include=\"Vendor\" Condition=\"false\"><HintPath>../Vendor.dll</HintPath></Reference></ItemGroup></Project>")]
    [InlineData("<Project><ItemGroup><Reference Include=\"Vendor\"><HintPath Condition=\"false\">../Vendor.dll</HintPath></Reference></ItemGroup></Project>")]
    [InlineData("<Project><ItemGroup><Reference Include=\"Vendor\"><HintPath>$(LibraryRoot)/Vendor.dll</HintPath></Reference></ItemGroup></Project>")]
    public void UnevaluatedReferencesAreReportedInsteadOfInventingDependencies(string xml)
    {
        using var project = new TempProject(("App/App.csproj", xml),
            ("App/Order.cs", "namespace Shop; public class Order : Vendor.Entity { }"));
        Emit(project, "Vendor.dll", "namespace Vendor; public class Entity { }");
        var log = new List<string>();

        Assert.Empty(Scan(project, log.Add).Edges);
        Assert.Contains(log, line => line.StartsWith("Reference needs MSBuild evaluation:", StringComparison.Ordinal));
    }

    [Fact]
    public void MalformedProjectReferencesAreReportedWithoutDiscardingSource()
    {
        using var project = new TempProject(("App/App.csproj", "<Project>"),
            ("App/Order.cs", "namespace Shop; public class Order { }"));
        var log = new List<string>();

        Assert.Equal("Shop.Order", Assert.Single(Scan(project, log.Add).Types).Id);
        Assert.Contains(log, line => line.StartsWith("Cannot read assembly references from", StringComparison.Ordinal));
    }

    [Fact]
    public void EmptyHintsAndUnrelatedItemsAreNotAssemblyReferences()
    {
        using var project = new TempProject(("App/App.csproj", """
            <Project><ItemGroup>
              <Reference Include="Empty"><HintPath> </HintPath></Reference>
              <Reference Include="Bare" />
              <ProjectReference Include="Sibling.csproj"><HintPath>../Vendor.dll</HintPath></ProjectReference>
              <Content Include="Vendor.dll"><HintPath>../Vendor.dll</HintPath></Content>
            </ItemGroup></Project>
            """), ("App/Order.cs", "namespace Shop; public class Order : Vendor.Entity { }"));
        Emit(project, "Vendor.dll", "namespace Vendor; public class Entity { }");

        Assert.Empty(Scan(project).Edges);
    }

    [Fact]
    public void RestoredNugetAssetsStillBindAlongsideExplicitReferences()
    {
        using var project = new TempProject(("App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />"),
            ("App/Order.cs", "namespace Shop; public class Order : Vendor.Entity { }"));
        Emit(project, "packages/vendor/1.0/lib/Vendor.dll", "namespace Vendor; public class Entity { }");
        var packageRoot = Path.Combine(project.Root, "packages").Replace('\\', '/');
        project.Write("App/obj/project.assets.json", $$"""
            {
              "packageFolders": { "{{packageRoot}}": {} },
              "libraries": { "Vendor/1.0": { "path": "vendor/1.0" } },
              "targets": { "net10.0": { "Vendor/1.0": { "type": "package", "compile": { "lib/Vendor.dll": {} } } } }
            }
            """);

        Assert.Contains(Scan(project).Edges, edge => edge.To == "x:Vendor");
    }

    [Fact]
    public void DuplicateAssemblyNamesChooseProjectsInOrdinalOrder()
    {
        using var project = new TempProject(("Z/Z.csproj", """
            <Project><ItemGroup><Reference Include="Vendor"><HintPath>Vendor.dll</HintPath></Reference></ItemGroup></Project>
            """), ("A/A.csproj", """
            <Project><ItemGroup><Reference Include="Vendor"><HintPath>Vendor.dll</HintPath></Reference></ItemGroup></Project>
            """));
        Emit(project, "Z/Vendor.dll", "namespace Vendor; public class Z { }");
        Emit(project, "A/Vendor.dll", "namespace Vendor; public class A { }");

        var references = References.For([Path.Combine(project.Root, "Z"), Path.Combine(project.Root, "A")]);

        Assert.Equal(Path.Combine(project.Root, "A", "Vendor.dll"),
            Assert.Single(references, reference => Path.GetFileName(reference.Display) == "Vendor.dll").Display);
    }

    [Fact]
    public void AHintCannotDuplicateTheRunningFramework()
    {
        var core = typeof(object).Assembly.Location;
        var name = Path.GetFileName(core);
        using var project = new TempProject(("App/App.csproj", $$"""
            <Project><ItemGroup><Reference Include="Core"><HintPath>../{{name}}</HintPath></Reference></ItemGroup></Project>
            """));
        File.Copy(core, Path.Combine(project.Root, name));
        var runtime = References.For([]).Where(reference => Path.GetFileName(reference.Display) == name).Select(reference => reference.Display).ToList();

        var references = References.For([Path.Combine(project.Root, "App")]);

        Assert.Equal(runtime, references.Where(reference => Path.GetFileName(reference.Display) == name).Select(reference => reference.Display));
    }

    [Fact]
    public void ChangedAssetsReplaceThePreviousPackageReferences()
    {
        using var project = new TempProject(("App/App.csproj", "<Project />"), ("App/Order.cs", """
            namespace Shop;
            public class OriginalOrder : Vendor.Original { }
            public class RevisedOrder : Vendor.Revised { }
            """));
        Emit(project, "packages/vendor/1.0/Original.dll", "namespace Vendor; public class Original { }");
        Emit(project, "packages/vendor/1.0/Revised.dll", "namespace Vendor; public class Revised { }");
        var packageRoot = Path.Combine(project.Root, "packages").Replace('\\', '/');
        var json = $$"""
            {
              "packageFolders": { "{{packageRoot}}": {} },
              "libraries": { "Vendor/1.0": { "path": "vendor/1.0" } },
              "targets": { "net10.0": { "Vendor/1.0": { "type": "package", "compile": { "Original.dll": {} } } } }
            }
            """;
        var assets = project.Write("App/obj/project.assets.json", json);
        var stamp = File.GetLastWriteTimeUtc(assets);
        Assert.Equal("Shop.OriginalOrder", Assert.Single(Scan(project).Edges).From);

        project.Write("App/obj/project.assets.json", json.Replace("Original.dll", "Revised.dll", StringComparison.Ordinal));
        File.SetLastWriteTimeUtc(assets, stamp.AddSeconds(2));

        Assert.Equal("Shop.RevisedOrder", Assert.Single(Scan(project).Edges).From);
    }

    private static CodeModel Scan(TempProject project, Action<string>? progress = null) =>
        new CSharpScanner().Scan(new ScanRequest(project.Root, Path.Combine(project.Root, "App"), [], "Shop", ["Vendor"], "Shop"), progress);

    private static void Emit(TempProject project, string file, string source)
    {
        var path = project.Write(file, "");
        var compilation = CSharpCompilation.Create(Path.GetFileNameWithoutExtension(file), [CSharpSyntaxTree.ParseText(source)],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)], new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var result = compilation.Emit(path);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
    }
}
