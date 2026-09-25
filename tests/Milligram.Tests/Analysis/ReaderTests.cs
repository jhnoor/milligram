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
            ("src/App/bin/Copy.csproj", "<Project />"));

        var projects = new DotNetProjectLocator().Find(project.Root);

        Assert.Equal(["App", "App.Tests"], projects.Select(p => p.Name));
        Assert.False(projects[0].IsTest);
        Assert.True(projects[1].IsTest);
        Assert.Equal(projects[0].Path, Assert.Single(projects[1].References));
    }
}
