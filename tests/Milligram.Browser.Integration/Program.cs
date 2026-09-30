using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Playwright;
using Milligram.Application;
using static Microsoft.Playwright.Assertions;

namespace Milligram.Browser.Integration;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        await using var viewer = new ViewerFixture();
        await viewer.Start();
        if (args.Contains("--serve", StringComparer.Ordinal))
        {
            Console.WriteLine(viewer.Url);
            using var stop = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
            try { await Task.Delay(Timeout.Infinite, stop.Token); }
            catch (OperationCanceledException) { }
            return 0;
        }
        var name = args.FirstOrDefault() ?? "chromium";
        using var playwright = await Playwright.CreateAsync();
        var engine = name switch { "firefox" => playwright.Firefox, "webkit" => playwright.Webkit, _ => playwright.Chromium };
        await using var browser = await engine.LaunchAsync(new()
        {
            Headless = true,
            Channel = name is "chrome" or "msedge" ? name : null,
        });
        await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1440, Height = 1000 } });
        var page = await context.NewPageAsync();
        var errors = new ConcurrentQueue<string>();
        page.PageError += (_, error) => errors.Enqueue(error);
        page.SetDefaultTimeout(15000);
        Assertions.SetDefaultExpectTimeout(15000);
        Directory.CreateDirectory("artifacts/browser");
        try
        {
            await Smoke(page, context, viewer);
            Require(errors.IsEmpty, "Browser script errors: " + string.Join("\n", errors));
            await page.ScreenshotAsync(new() { Path = $"artifacts/browser/{name}.png", FullPage = true });
            Console.WriteLine($"PASS {name}: output, input, keyboard isolation, bracketed paste, mail, resize, replay, controls, popout and source links");
            return 0;
        }
        catch (Exception error)
        {
            await page.ScreenshotAsync(new() { Path = $"artifacts/browser/{name}-failure.png", FullPage = true });
            Console.Error.WriteLine(error);
            Console.Error.WriteLine("Received input: " + System.Text.Json.JsonSerializer.Serialize(viewer.Companion.Terminal.Received));
            Console.Error.WriteLine(string.Join("\n", errors));
            return 1;
        }
    }

    private static async Task Smoke(IPage page, IBrowserContext context, ViewerFixture viewer)
    {
        await page.GotoAsync(viewer.Url);
        var panel = page.Locator("#agent-panel");
        var text = panel.Locator(".xterm-rows");
        var input = panel.Locator(".xterm-helper-textarea");
        await Expect(panel).ToBeVisibleAsync();
        await Expect(text).ToContainTextAsync("READY 1: Grüße 漢字 🐱");
        await Expect(panel.Locator(".agent-state")).ToHaveTextAsync("Running");

        await input.FocusAsync();
        await page.Keyboard.TypeAsync("hello");
        await page.Keyboard.InsertTextAsync(" 漢字 🐱");
        await Expect(text).ToContainTextAsync("hello 漢字 🐱");
        await Until(() => viewer.Companion.Terminal.Received.Contains("hello 漢字 🐱", StringComparison.Ordinal));
        var camera = await page.Locator("#viewport").GetAttributeAsync("transform");
        await page.Keyboard.PressAsync("Escape");
        await Expect(input).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("ArrowUp");
        await page.Keyboard.PressAsync("Control+f");
        await page.Keyboard.PressAsync("Control+c");
        await Until(() => viewer.Companion.Terminal.Received.Contains('\u0003'));
        Require(camera == await page.Locator("#viewport").GetAttributeAsync("transform"), "Terminal keys moved the diagram.");
        await input.EvaluateAsync("""
            element => {
              const paste = new ClipboardEvent('paste', { bubbles: true, cancelable: true });
              // Firefox restricts synthetic DataTransfer data. Supply the paste event's payload without touching the OS clipboard.
              Object.defineProperty(paste, 'clipboardData', { value: { getData: format => format === 'text/plain' ? 'paste one\npaste two' : '' } });
              element.dispatchEvent(paste);
            }
            """);
        await Until(() => viewer.Companion.Terminal.Received.Contains("\u001b[200~paste one\rpaste two\u001b[201~", StringComparison.Ordinal));

        await page.Locator("#ask").FillAsync("Explain this Order");
        await page.Locator("#send").ClickAsync();
        await Expect(page.Locator("#ask")).ToHaveValueAsync("");
        await Until(() => viewer.Companion.Terminal.Received.Contains(AgentBriefing.Doorbell + "\r", StringComparison.Ordinal));
        var mail = viewer.Workspace.ToAgent.Take().Single(m => m.Op == "message");
        Require(mail.Data["text"]!.GetValue<string>() == "Explain this Order", "Inspector mail lost its text.");

        var divider = panel.GetByRole(AriaRole.Separator);
        await divider.FocusAsync();
        var oldHeight = await panel.EvaluateAsync<double>("element => element.getBoundingClientRect().height");
        await page.Keyboard.PressAsync("ArrowUp");
        var height = await panel.EvaluateAsync<double>("element => element.getBoundingClientRect().height");
        Require(height == oldHeight + 24, "The accessible divider did not resize the panel.");
        await panel.Locator(".agent-toggle").ClickAsync();
        await Expect(panel).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("collapsed"));
        Require(viewer.Companion.IsRunning(), "Collapsing stopped the agent.");
        await viewer.Companion.Terminal.Produce("\r\nREPLAY AFTER DETACH\r\n");
        await page.Keyboard.PressAsync("Control+Backquote");
        await Expect(text).ToContainTextAsync("REPLAY AFTER DETACH");
        await Expect(input).ToBeFocusedAsync();
        await page.ReloadAsync();
        await Expect(text).ToContainTextAsync("REPLAY AFTER DETACH");
        Require(height == await panel.EvaluateAsync<double>("element => element.getBoundingClientRect().height"), "Panel height was not remembered.");

        // More than the transport's 128 KiB window must drain through xterm's parse acknowledgements.
        await viewer.Companion.Terminal.Produce(string.Concat(Enumerable.Repeat("flow control output 0123456789\r\n", 6000)) + "FLOW COMPLETE\r\nOrder.cs:2\r\n");
        await Expect(text).ToContainTextAsync("FLOW COMPLETE");
        var link = text.Locator(":scope > div").Filter(new() { HasText = "Order.cs:2" }).Last;
        var bounds = await link.BoundingBoxAsync() ?? throw new InvalidOperationException("The source link is not visible.");
        await page.Mouse.MoveAsync(bounds.X + 20, bounds.Y + 8);
        await page.Keyboard.DownAsync("Control");
        await page.Mouse.ClickAsync(bounds.X + 20, bounds.Y + 8);
        await page.Keyboard.UpAsync("Control");
        await Expect(page.Locator("#source")).ToBeVisibleAsync();
        await Expect(page.Locator("#source")).ToContainTextAsync("public class Order");
        await page.Locator("#source .close").ClickAsync();

        var popout = await context.RunAndWaitForPageAsync(() => panel.Locator(".agent-popout-button").ClickAsync());
        await Expect(popout.Locator(".xterm-rows")).ToContainTextAsync("FLOW COMPLETE");
        await popout.Locator(".xterm-helper-textarea").FocusAsync();
        await popout.Keyboard.TypeAsync("from popout");
        await Expect(popout.Locator(".xterm-rows")).ToContainTextAsync("from popout");
        await popout.CloseAsync();
        await page.BringToFrontAsync();
        Require(viewer.Companion.Starts == 1, "The popout started a second session.");

        await panel.Locator(".agent-restart").ClickAsync();
        await Expect(text).ToContainTextAsync("READY 2:");
        Require(viewer.Companion.Starts == 2, "Restart did not replace the session.");
        await panel.Locator(".agent-stop").ClickAsync();
        await Expect(panel.Locator(".agent-state")).ToHaveTextAsync("Stopped");
        await panel.Locator(".agent-start").ClickAsync();
        await Expect(text).ToContainTextAsync("READY 3:");
        await panel.Locator(".agent-reader input").CheckAsync();
        await Expect(panel.Locator(".xterm-accessibility")).ToBeAttachedAsync();
        await page.ReloadAsync();
        await Expect(panel.Locator(".agent-reader input")).ToBeCheckedAsync();
        await Expect(text).ToContainTextAsync("READY 3:");

        Require(await page.EvaluateAsync<bool>("() => !JSON.stringify(localStorage).includes('milligram-terminal.') && !location.href.includes('milligram-terminal.')"),
            "The terminal token leaked into persistent browser state.");
        await viewer.Companion.Terminal.Produce("\r\n\u001b]52;c;c2VjcmV0\u0007<img src=x onerror=alert(1)>\r\nSAFE OUTPUT\r\n");
        await Expect(text).ToContainTextAsync("SAFE OUTPUT");
        await Expect(text).ToContainTextAsync("<img src=x onerror=alert(1)>");
        Require(await panel.Locator("img").CountAsync() == 0, "Terminal text was interpreted as HTML.");

        viewer.Workspace.EditPolicy(p => p with { Agent = p.Agent with { Terminal = "none" } });
        await page.ReloadAsync();
        await Expect(panel).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("collapsed"));
        await panel.Locator(".agent-toggle").ClickAsync();
        await Expect(text).ToContainTextAsync("SAFE OUTPUT");
        viewer.Workspace.EditPolicy(p => p with { Agent = p.Agent with { Terminal = "auto" } });

        await page.RouteAsync("**/api/meta*", async route =>
        {
            var response = await route.FetchAsync();
            var meta = System.Text.Json.Nodes.JsonNode.Parse(await response.TextAsync())!;
            meta["root"] = "another-project";
            await route.FulfillAsync(new() { Response = response, Body = meta.ToJsonString() });
        });
        await panel.Locator(".agent-toggle").ClickAsync();
        await panel.Locator(".agent-toggle").ClickAsync();
        await Expect(panel.Locator(".agent-notice")).ToContainTextAsync("The project changed");
        await Expect(panel.Locator(".agent-restart")).ToBeDisabledAsync();
        await Expect(panel.Locator(".agent-stop")).ToBeDisabledAsync();
        await page.UnrouteAsync("**/api/meta*");
        await page.ReloadAsync();
        await Expect(text).ToContainTextAsync("SAFE OUTPUT");
        await CheckLinks(page);
    }

    private static async Task CheckLinks(IPage page)
    {
        var result = await page.EvaluateAsync<string[]>("""
            async () => {
              const { Terminal } = await import('/lib/xterm/xterm.mjs');
              const { fileLinks } = await import('/terminal-links.js');
              const terminal = new Terminal({ cols: 28, rows: 20 });
              const root = document.createElement('div');
              document.body.appendChild(root);
              terminal.open(root);
              try {
                await new Promise(done => terminal.write('漢 🐱 "src/My Folder/Order.cs":12\r\nC:\\repo\\My Folder\\Order.cs(23,4)\r\n/tmp/My Folder/Order.cs:34:2\r\ntests/VeryLongNamespace/Unicode漢字/Order.cs:45\r\n', done));
                const opened = new Set();
                const provider = fileLinks(terminal, (file, line) => opened.add(file + ':' + line));
                for (let row = 1; row <= terminal.buffer.active.length; row++) {
                  provider.provideLinks(row, links => {
                    for (const link of links) link.activate({ ctrlKey: true, preventDefault() {} });
                  });
                }
                return [...opened];
              } finally { terminal.dispose(); root.remove(); }
            }
            """);
        var expected = new[] { "src/My Folder/Order.cs:12", @"C:\repo\My Folder\Order.cs:23", "/tmp/My Folder/Order.cs:34", "tests/VeryLongNamespace/Unicode漢字/Order.cs:45" };
        Require(result.SequenceEqual(expected), "Wrapped file links were parsed incorrectly: " + string.Join(", ", result));
    }

    private static async Task Until(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("The terminal fixture did not receive the expected input.");
            await Task.Delay(20);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
