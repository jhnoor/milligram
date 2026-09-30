using Milligram.Analysis.CSharp;
using Milligram.Application;

namespace Milligram.Tests.Analysis;

public class ImplicitUsingsTests
{
    [Fact]
    public void TheNewestGeneratedUsingsUnderObjTakePrecedence()
    {
        using var project = new TempProject(("App.csproj", "<Project />"),
            ("obj/Debug/App.GlobalUsings.g.cs", "global using System.Net.Http;"),
            ("obj/Release/App.GlobalUsings.g.cs", "global using System.Text.Json;"),
            ("App.GlobalUsings.g.cs", "global using System.Text.Json;"),
            ("src/Order.cs", "namespace Shop; public class Order { public HttpClient? Client { get; } public JsonSerializerOptions? Options { get; } }"));
        var time = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Path.Combine(project.Root, "obj/Release/App.GlobalUsings.g.cs"), time);
        File.SetLastWriteTimeUtc(Path.Combine(project.Root, "obj/Debug/App.GlobalUsings.g.cs"), time.AddMinutes(1));
        File.SetLastWriteTimeUtc(Path.Combine(project.Root, "App.GlobalUsings.g.cs"), time.AddMinutes(2));

        var model = new CSharpScanner().Scan(new ScanRequest(project.Root, Path.Combine(project.Root, "src"), [], "Shop",
            ["System.Net.Http", "System.Text.Json"], "Shop"));

        Assert.Contains(model.Edges, edge => edge.To == "x:System.Net.Http");
        Assert.DoesNotContain(model.Edges, edge => edge.To == "x:System.Text.Json");
    }

    [Fact]
    public void LooseSourceFilesUseTheSdkDefaults()
    {
        using var project = new TempProject(("Order.cs", "namespace Shop; public class Order { public HttpClient? Client { get; } }"));

        var model = new CSharpScanner().Scan(new ScanRequest(project.Root, project.Root, [], "Shop", ["System.Net.Http"], "Shop"));

        Assert.Contains(model.Edges, edge => edge.To == "x:System.Net.Http");
    }

    [UnixFact]
    public void GeneratedUsingDiscoveryDoesNotFollowDirectoryLinks()
    {
        using var project = new TempProject(("App.csproj", "<Project />"),
            ("obj/Debug/App.GlobalUsings.g.cs", "global using System.Net.Http;"),
            ("Order.cs", "namespace Shop; public class Order { public HttpClient Client { get; } = new(); }"));
        var obj = Path.Combine(project.Root, "obj");
        Directory.CreateSymbolicLink(Path.Combine(obj, "cycle"), obj);

        var model = new CSharpScanner().Scan(new ScanRequest(project.Root, project.Root, [], "Shop", ["System.Net.Http"], "Shop"));

        Assert.Contains(model.Edges, edge => edge.To == "x:System.Net.Http");
    }

    [Fact]
    public void FallbackUsingsIgnoreOtherFilesAndChooseAStableProject()
    {
        using var project = new TempProject(("Z.csproj", "<Project><PropertyGroup><ImplicitUsings>disable</ImplicitUsings></PropertyGroup></Project>"),
            ("A.csproj", "<Project><PropertyGroup><ImplicitUsings>true</ImplicitUsings></PropertyGroup></Project>"),
            ("0-not-a-project.xml", "<Project><PropertyGroup><ImplicitUsings>disable</ImplicitUsings></PropertyGroup></Project>"),
            ("Order.cs", "namespace Shop; public class Order { public HttpClient Client { get; } = new(); }"));

        var model = new CSharpScanner().Scan(new ScanRequest(project.Root, project.Root, [], "Shop", ["System.Net.Http"], "Shop"));

        Assert.Contains(model.Edges, edge => edge.To == "x:System.Net.Http");
    }

    [Theory]
    [InlineData("<ImplicitUsings>enable</ImplicitUsings>", true)]
    [InlineData("<ImplicitUsings>true</ImplicitUsings>", true)]
    [InlineData("<ImplicitUsings>TRUE</ImplicitUsings>", true)]
    [InlineData("<ImplicitUsings >true</ImplicitUsings>", true)]
    [InlineData("<ImplicitUsings> TRUE </ImplicitUsings>", false)]
    [InlineData("<ImplicitUsings>enable\n</ImplicitUsings>", false)]
    [InlineData("<ImplicitUsings>disable</ImplicitUsings>", false)]
    [InlineData("<ImplicitUsings>false</ImplicitUsings>", false)]
    [InlineData("<!-- <ImplicitUsings>enable</ImplicitUsings> -->", false)]
    [InlineData("<ImplicitUsings>enable</ImplicitUsings><ImplicitUsings>disable</ImplicitUsings>", false)]
    [InlineData("<ImplicitUsings Condition=\"false\">enable</ImplicitUsings>", false)]
    public void UnbuiltProjectsReadExplicitUsingSettingsAsXml(string setting, bool expected)
    {
        using var project = new TempProject(("App.csproj", $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>{setting}</PropertyGroup></Project>"),
            ("Order.cs", "namespace Shop; public class Order { public HttpClient Client { get; } = new(); }"));

        var model = new CSharpScanner().Scan(new ScanRequest(project.Root, project.Root, [], "Shop", ["System.Net.Http"], "Shop"));

        Assert.Equal(expected, model.Edges.Any(edge => edge.To == "x:System.Net.Http"));
    }

    [Theory]
    [InlineData("<Project Sdk=\"Microsoft.NET.Sdk\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\"><PropertyGroup>", true)]
    [InlineData("<Project><PropertyGroup Condition=\"false\">", false)]
    public void NamespacedPropertiesWorkAndUnevaluatedGroupsAreSkipped(string opening, bool expected)
    {
        using var project = new TempProject(("App.csproj", opening + "<ImplicitUsings>true</ImplicitUsings></PropertyGroup></Project>"),
            ("Order.cs", "namespace Shop; public class Order { public HttpClient Client { get; } = new(); }"));

        var model = new CSharpScanner().Scan(new ScanRequest(project.Root, project.Root, [], "Shop", ["System.Net.Http"], "Shop"));

        Assert.Equal(expected, model.Edges.Any(edge => edge.To == "x:System.Net.Http"));
    }

    [Theory]
    [InlineData("<Project Sdk=\"Microsoft.NET.Sdk.Web\">", "", true)]
    [InlineData("<Project>", "<Sdk Name=\"Microsoft.NET.Sdk.Web\" />", true)]
    [InlineData("<Project Sdk=\"Microsoft.NET.Sdk\">", "<!-- Microsoft.NET.Sdk.Web -->", false)]
    public void WebDefaultsComeFromSdkDeclarationsInsteadOfComments(string opening, string content, bool expected)
    {
        using var project = new TempProject(("App.csproj", opening + content + "<PropertyGroup><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>"),
            ("Order.cs", "namespace Shop; public class Order { public WebApplication? App { get; } }"));

        var model = new CSharpScanner().Scan(new ScanRequest(project.Root, project.Root, [], "Shop", ["Microsoft.AspNetCore"], "Shop"));

        Assert.Equal(expected, model.Edges.Any(edge => edge.To == "x:Microsoft.AspNetCore"));
    }
}
