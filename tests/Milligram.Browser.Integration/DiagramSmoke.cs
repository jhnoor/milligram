using System.Diagnostics;
using Microsoft.Playwright;
using Milligram.Domain.Hierarchy;
using static Microsoft.Playwright.Assertions;

namespace Milligram.Browser.Integration;

internal static class DiagramSmoke
{
    public static async Task Run(IPage page, ViewerFixture viewer, bool cached)
    {
        if (!cached) await page.SetViewportSizeAsync(1000, 700);
        var time = Stopwatch.StartNew();
        await page.GotoAsync(viewer.Url);
        await Ready(page);
        if (!cached)
        {
            await Expect(page.Locator("#empty")).ToBeVisibleAsync();
            while (viewer.Viewers == 0 && time.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(20);
            Require(viewer.Viewers > 0, "Viewer did not subscribe to model updates.");
            // The model arrives over SSE after the empty first screen has already been laid out.
            viewer.GenerateDiagram();
            time.Restart();
            await Expect(page.Locator("#overview")).ToBeVisibleAsync();
            await Ready(page);
        }
        Console.WriteLine($"Diagram first layout: {time.Elapsed.TotalMilliseconds:F0} ms");
        var root = viewer.Workspace.View(null, null);
        var model = viewer.Workspace.Model;
        var tree = viewer.Workspace.Tree(null);
        var references = root.Nodes.Sum(n => n.InternalReferences) + root.Edges.Sum(e => e.Pairs.Sum(p => p.Count));
        var violations = root.Nodes.Sum(n => n.InternalViolations) + root.Edges.Sum(e => e.Pairs.Where(p => p.Violating).Sum(p => p.Count));
        Require(references == model.Edges.Sum(e => e.Count), "Root overview lost reference counts.");
        Require(violations == model.Edges.Where(e => DependencyRule.IsViolating(e.Kind, tree.LevelOfType(e.From), tree.LevelOfType(e.To))).Sum(e => e.Count), "Root overview lost violation counts.");
        Console.WriteLine($"Root: {root.Nodes.Count} nodes, {root.Edges.Count} bundles, {references} references, {violations} violations (including internal references)");
        await Expect(page.Locator("#overview")).ToBeVisibleAsync();
        var count = await page.Locator("#canvas .node").CountAsync();
        Require(count <= 33, $"Overview rendered {count} nodes.");
        await AssertFits(page);
        await page.ScreenshotAsync(new() { Path = $"artifacts/browser/{(cached ? "cached" : "wide")}-overview.png", FullPage = true });

        // Each page owns its camera; returning must not fit over the user's pan.
        await page.Locator("#canvas").FocusAsync();
        await page.Keyboard.PressAsync("ArrowRight");
        var camera = await page.Locator("#viewport").GetAttributeAsync("transform");
        time.Restart();
        await page.Locator("#page-next").ClickAsync();
        await Expect(page.Locator("#page-count")).ToContainTextAsync("21–40");
        await Ready(page);
        Console.WriteLine($"Diagram next page: {time.Elapsed.TotalMilliseconds:F0} ms");
        await page.Locator("#page-prev").ClickAsync();
        await Expect(page.Locator("#page-count")).ToContainTextAsync("1–20");
        await Ready(page);
        Require(camera == await page.Locator("#viewport").GetAttributeAsync("transform"), "Paging lost the saved camera.");

        var target = cached ? "Microsoft" : "Global499";
        await page.Locator("#level-query").FillAsync(target);
        time.Restart();
        await page.Locator("#level-search button[type=submit]").ClickAsync();
        await Expect(page.Locator("#page-count")).ToContainTextAsync("total");
        await Ready(page);
        Console.WriteLine($"Diagram find {target}: {time.Elapsed.TotalMilliseconds:F0} ms");
        if (cached)
        {
            var microsoft = page.Locator("#canvas .node[data-id='ns:Microsoft']");
            await microsoft.DblClickAsync();
            await Expect(page.Locator("#crumbs .here")).ToHaveTextAsync("Microsoft");
            await Ready(page);
            Require(await page.Locator("#canvas .node").CountAsync() <= 92, "Drilled view exceeded its bound.");
            Console.WriteLine($"Diagram drill Microsoft: {time.Elapsed.TotalMilliseconds:F0} ms since search");
            foreach (var path in new[] { "Microsoft.AspNetCore", "Microsoft.AspNetCore.Mvc", "Microsoft.AspNetCore.Mvc.Razor" })
            {
                var label = path.Split('.').Last();
                await page.Locator("#level-query").FillAsync(label);
                await page.Locator("#level-search button[type=submit]").ClickAsync();
                await Ready(page);
                await page.Locator($"#canvas .node[data-id='ns:{path}']").DblClickAsync();
                await Expect(page.Locator("#crumbs .here")).ToHaveTextAsync(label);
                await Ready(page);
            }
            await page.Locator("#level-query").FillAsync("RenderAsyncDelegate");
            await page.Locator("#level-search button[type=submit]").ClickAsync();
            await Ready(page);
            var delegates = page.Locator("#canvas .node.type").Filter(new() { HasText = "RenderAsyncDelegate" });
            await Expect(delegates).ToHaveCountAsync(3);
            await delegates.First.DblClickAsync();
            await Expect(page.Locator("#card h3")).ToHaveTextAsync("RenderAsyncDelegate");
            await page.Locator("#card .card-meta a").Last.ClickAsync();
            await Expect(page.Locator("#source")).ToContainTextAsync("delegate");
        }
        else
        {
            await page.Locator("#canvas .node[data-id='t:Global499']").DblClickAsync();
            await Expect(page.Locator("#card h3")).ToHaveTextAsync("Global499");
            await page.Locator("#card tr.member").Filter(new() { HasText = "Value" }).ClickAsync();
            await Expect(page.Locator("#source")).ToContainTextAsync("Value() => 499");
            await page.Locator("#source .close").ClickAsync();
            await page.Locator("#card .close").ClickAsync();
            await page.Locator("#clear-query").ClickAsync();
            await Expect(page.Locator("#page-count")).ToContainTextAsync("1–20 of 501");
            await Ready(page);
            Require(camera == await page.Locator("#viewport").GetAttributeAsync("transform"), "Filtering lost the root camera.");
            await page.Locator("#arrows [data-v=detailed]").ClickAsync();
            await Ready(page);
            await AssertFits(page);
            await page.Locator("#arrows [data-v=auto]").ClickAsync();
            await Ready(page);
            Require(camera == await page.Locator("#viewport").GetAttributeAsync("transform"), "Changing layouts lost the overview camera.");
            await page.Locator("#canvas .node[data-id='ns:Wide']").DblClickAsync();
            await Expect(page.Locator("#crumbs .here")).ToHaveTextAsync("Wide");
            await Ready(page);
            await Expect(page.Locator("#page-count")).ToContainTextAsync("1–20 of 120");
            await page.Locator("#canvas").FocusAsync();
            await page.Keyboard.PressAsync("ArrowDown");
            var wideCamera = await page.Locator("#viewport").GetAttributeAsync("transform");
            await page.Locator("#up").ClickAsync();
            await Expect(page.Locator("#crumbs .here")).Not.ToHaveTextAsync("Wide");
            await Ready(page);
            Require(camera == await page.Locator("#viewport").GetAttributeAsync("transform"), "Drilling lost the root camera.");
            await page.GoBackAsync();
            await Expect(page.Locator("#crumbs .here")).ToHaveTextAsync("Wide");
            await Ready(page);
            Require(wideCamera == await page.Locator("#viewport").GetAttributeAsync("transform"), "History lost the namespace camera.");
            await page.Locator("#new-proposal").ClickAsync();
            await Expect(page.Locator("#banner")).ToBeVisibleAsync();
            await Ready(page);
            await page.Locator("#contexts li").Filter(new() { HasText = "Real diagram" }).ClickAsync();
            await Expect(page.Locator("#banner")).ToBeHiddenAsync();
            await Ready(page);
            Require(camera == await page.Locator("#viewport").GetAttributeAsync("transform"), "Switching contexts lost the real diagram camera.");
        }
    }

    private static Task Ready(IPage page) => Expect(page.Locator("#canvas")).ToHaveAttributeAsync("aria-busy", "false");

    private static async Task AssertFits(IPage page)
    {
        var fits = await page.EvaluateAsync<bool>("""
            () => {
              const stage = document.querySelector('#stage').getBoundingClientRect();
              return [...document.querySelectorAll('#canvas .node > .body')].every(n => {
                const b = n.getBoundingClientRect();
                return b.left >= stage.left - 1 && b.top >= stage.top - 1 && b.right <= stage.right + 1 && b.bottom <= stage.bottom + 1;
              });
            }
            """);
        Require(fits, "The first complete diagram did not fit its viewport.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
