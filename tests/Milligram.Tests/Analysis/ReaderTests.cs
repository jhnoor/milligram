using Milligram.Analysis.Coverage;
using Milligram.Analysis.DotNet;
using Milligram.Analysis.Mutation;
using Milligram.Domain.Metrics;

namespace Milligram.Tests.Analysis;

public class CoberturaReaderTests
{
    [Fact]
    public void ReadsLineHitsRelativeToTheRoot()
    {
        using var project = new TempProject(("src/A.cs", "class A {}"), ("src/B.cs", "class B {}"));
        var report = project.Write("coverage.cobertura.xml", $"""
            <?xml version="1.0"?>
            <coverage>
              <sources><source>{project.Root}/</source></sources>
              <packages><package name="App"><classes>
                <class name="A" filename="src/A.cs"><methods><method name="M"><lines><line number="3" hits="9"/></lines></method></methods>
                  <lines><line number="3" hits="2"/><line number="4" hits="0"/></lines></class>
                <class name="A/Inner" filename="src/A.cs"><lines><line number="4" hits="1"/></lines></class>
                <class name="B" filename="{project.Root}/src/B.cs"><lines><line number="1" hits="0"/></lines></class>
                <class name="Outside" filename="/elsewhere/C.cs"><lines><line number="1" hits="1"/></lines></class>
              </classes></package></packages>
            </coverage>
            """);

        var hits = new CoberturaReader().Read([report], project.Root);

        Assert.Equal(new Dictionary<int, int> { [3] = 2, [4] = 1 }, hits.For("src/A.cs"));
        Assert.Equal(new Dictionary<int, int> { [1] = 0 }, hits.For("src/B.cs"));
        Assert.Equal(2, hits.Files.Count);
    }
}

public class StrykerReportReaderTests
{
    [Fact]
    public void ReadsMutantsWithStatusAndLocation()
    {
        using var project = new TempProject();
        var report = project.Write("mutation-report.json", """
            {
              "schemaVersion": "2",
              "projectRoot": "PROJECT/src/App",
              "files": {
                "Domain/Order.cs": {
                  "language": "cs",
                  "mutants": [
                    { "id": "1", "mutatorName": "Equality mutation", "status": "Killed", "location": { "start": { "line": 12, "column": 17 }, "end": { "line": 12, "column": 19 } } },
                    { "id": "2", "mutatorName": "Boolean mutation", "status": "NoCoverage", "location": { "start": { "line": 20, "column": 5 }, "end": { "line": 20, "column": 9 } } },
                    { "id": "3", "mutatorName": "String mutation", "status": "Weird", "location": { "start": { "line": 1, "column": 1 }, "end": { "line": 1, "column": 2 } } }
                  ]
                }
              }
            }
            """.Replace("PROJECT", project.Root));

        var mutants = new StrykerReportReader().Read(report, project.Root, "/unused");

        Assert.Equal(3, mutants.Count);
        Assert.Equal(new Mutant("src/App/Domain/Order.cs", 12, 17, MutantStatus.Killed, "Equality mutation"), mutants[0]);
        Assert.Equal(MutantStatus.NoCoverage, mutants[1].Status);
        Assert.Equal(MutantStatus.Pending, mutants[2].Status);
    }
}

public class DotNetProjectLocatorTests
{
    [Fact]
    public void FindsProjectsReferencesAndTests()
    {
        using var project = new TempProject(
            ("src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>"),
            ("tests/App.Tests/App.Tests.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.0.0" /></ItemGroup>
                  <ItemGroup><ProjectReference Include="..\..\src\App\App.csproj" /></ItemGroup>
                </Project>
                """),
            ("src/App/bin/Copy.csproj", "<Project />"),
            ("src/Lib/Lib.csproj", "<Project><PropertyGroup><IsPackable>true</IsPackable></PropertyGroup></Project>"),
            ("tests/Spec/Spec.csproj", "<Project><PropertyGroup><IsTestProject> True </IsTestProject></PropertyGroup></Project>"));

        var projects = new DotNetProjectLocator().Find(project.Root);

        Assert.Equal(["App", "Lib", "App.Tests", "Spec"], projects.Select(p => p.Name));
        Assert.Equal([false, false, true, true], projects.Select(p => p.IsTest));
        Assert.Equal(projects[0].Path, Assert.Single(projects[2].References));
    }

    [Fact]
    public void KnowsWhichProjectsAreRestoredAndWhichPackagesTheyUse()
    {
        using var project = new TempProject(
            ("named/Named.csproj", """<Project><ItemGroup><PackageReference Include="Coverlet.Collector" /></ItemGroup></Project>"""),
            ("restored/Restored.csproj", "<Project />"),
            ("restored/obj/project.assets.json", """{ "libraries": { "coverlet.collector/6.0.4": { "type": "package" }, "xunit/2.9.3": {} } }"""),
            ("without/Without.csproj", "<Project />"),
            ("without/obj/project.assets.json", """{ "libraries": { "xunit/2.9.3": {} } }"""),
            ("fresh/Fresh.csproj", "<Project />"),
            ("broken/Broken.csproj", "<Project />"),
            ("broken/obj/project.assets.json", "{ nope"),
            ("odd/Odd.csproj", "<Project />"),
            ("odd/obj/project.assets.json", "[]"));
        var locator = new DotNetProjectLocator();
        var projects = locator.Find(project.Root).ToDictionary(p => p.Name);

        Assert.Equal(["Broken", "Odd", "Restored", "Without"], projects.Values.Where(p => p.IsRestored).Select(p => p.Name).Order());
        Assert.Null(locator.UsesPackage(projects["Odd"], "coverlet.collector"));
        Assert.True(locator.UsesPackage(projects["Named"], "coverlet.collector"));
        Assert.True(locator.UsesPackage(projects["Restored"], "coverlet.collector"));
        Assert.False(locator.UsesPackage(projects["Without"], "coverlet.collector"));
        Assert.Null(locator.UsesPackage(projects["Fresh"], "coverlet.collector"));
        Assert.Null(locator.UsesPackage(projects["Broken"], "coverlet.collector"));
        Assert.False(locator.UsesPackage(projects["Restored"], "coverlet"));
    }
}
