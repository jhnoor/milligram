using System.Text.Json;
using Microsoft.Playwright;
using Milligram.Application;
using static Microsoft.Playwright.Assertions;

namespace Milligram.Browser.Integration;

/// <summary>Records startup stages without retaining terminal payloads, URLs or secret subprotocols.</summary>
internal static class StartupDiagnostics
{
    public static Task Install(IBrowserContext context) => context.AddInitScriptAsync(Script);

    public static async Task Capture(IPage page, string name)
    {
        try
        {
            var json = await page.EvaluateAsync<string>("() => JSON.stringify(window.milligramStartupSnapshot?.() ?? null)")
                .WaitAsync(TimeSpan.FromSeconds(3));
            var snapshot = JsonSerializer.Deserialize<JsonElement>(json, MilligramJson.Options);
            await File.WriteAllTextAsync($"artifacts/browser/{name}.json", JsonSerializer.Serialize(snapshot, MilligramJson.Options));
            if (snapshot.ValueKind == JsonValueKind.Object)
                Console.WriteLine($"STARTUP {name}: {snapshot.GetProperty("summary")}");
        }
        catch (Exception error) when (error is PlaywrightException or TimeoutException)
        {
            Console.Error.WriteLine($"STARTUP {name}: diagnostic snapshot unavailable ({error.GetType().Name}).");
        }
    }

    public static async Task<int> Repeat(IBrowser browser, string name, ViewerFixture viewer)
    {
        var rates = name is "firefox" or "webkit" ? new[] { 1 } : [1, 4];
        foreach (var rate in rates)
            for (var round = 1; round <= 16; round++)
            {
                await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1440, Height = 1000 } });
                await Install(context);
                var page = await context.NewPageAsync();
                page.SetDefaultTimeout(15000);
                var errors = 0;
                page.PageError += (_, _) => Interlocked.Increment(ref errors);
                if (rate > 1)
                {
                    var session = await context.NewCDPSessionAsync(page);
                    await session.SendAsync("Emulation.setCPUThrottlingRate", new Dictionary<string, object> { ["rate"] = rate });
                }
                var label = $"{name}-startup-{rate}x-{round:D2}";
                try
                {
                    await page.GotoAsync(viewer.Url);
                    await Expect(page.Locator("#agent-panel")).ToBeVisibleAsync();
                    await Expect(page.Locator("#agent-panel .xterm-rows")).ToContainTextAsync("READY 1: Grüße 漢字 🐱");
                    await Expect(page.Locator("#agent-panel .agent-state")).ToHaveTextAsync("Running");
                    await page.WaitForFunctionAsync("() => window.milligramStartupSnapshot().summary.parsedBytes > 0");
                    if (errors != 0) throw new InvalidOperationException("Startup emitted browser script errors.");
                    await Capture(page, label);
                }
                catch (Exception error)
                {
                    await Capture(page, label + "-before-screenshot");
                    await page.ScreenshotAsync(new() { Path = $"artifacts/browser/{label}-failure.png", FullPage = true });
                    await Capture(page, label + "-after-screenshot");
                    Console.Error.WriteLine(error);
                    return 1;
                }
            }
        Console.WriteLine($"PASS {name}: 16 fresh contexts at each CPU throttle rate ({string.Join(", ", rates.Select(rate => rate + "x"))}), with the 15-second startup assertion unchanged.");
        return 0;
    }

    private const string Script = """
        (() => {
          const events = [], limit = 256;
          let dropped = 0, sockets = 0, receivedBytes = 0, parsedBytes = 0;
          let firstOpen = null, firstReceipt = null, firstParse = null, firstGreeting = null;
          let frames = 0, lastFrame = null, maximumFrameGap = 0, lastRender = '';
          const now = () => Math.round(performance.now() * 10) / 10;
          const record = (stage, details = {}) => {
            if (events.length === limit) { events.shift(); dropped++; }
            events.push({ ms: now(), stage, ...details });
          };
          const NativeSocket = window.WebSocket;
          window.WebSocket = class extends NativeSocket {
            constructor(...args) {
              super(...args);
              const id = ++sockets;
              record('socket-created', { id });
              this.addEventListener('open', () => { firstOpen ??= now(); record('socket-open', { id }); });
              this.addEventListener('close', event => record('socket-close', { id, code: event.code }));
              this.addEventListener('error', () => record('socket-error', { id }));
              this.addEventListener('message', event => {
                if (!(event.data instanceof ArrayBuffer)) return;
                receivedBytes += event.data.byteLength;
                firstReceipt ??= now();
                record('output-received', { id, bytes: event.data.byteLength });
              });
            }
            send(data) {
              if (typeof data === 'string' && data.length < 512) {
                try {
                  const message = JSON.parse(data);
                  if (message.type === 'ack' && Number.isSafeInteger(message.bytes) && message.bytes > 0) {
                    parsedBytes += message.bytes;
                    firstParse ??= now();
                    record('output-parsed', { bytes: message.bytes });
                  } else if (message.type === 'resize' && Number.isSafeInteger(message.columns) && Number.isSafeInteger(message.rows)) {
                    record('terminal-resized', { columns: message.columns, rows: message.rows });
                  }
                } catch { }
              }
              return super.send(data);
            }
          };
          const rendered = () => {
            const rows = document.querySelector('#agent-panel .xterm-rows');
            const text = rows?.textContent ?? '';
            return { characters: text.length, greeting: text.includes('READY 1: Grüße 漢字 🐱') };
          };
          new MutationObserver(() => {
            const state = rendered(), value = JSON.stringify(state);
            if (state.greeting) firstGreeting ??= now();
            if (value !== lastRender) { lastRender = value; record('render-observed', state); }
          }).observe(document, { childList: true, characterData: true, subtree: true });
          const nativeFrame = window.requestAnimationFrame.bind(window);
          window.requestAnimationFrame = callback => nativeFrame(function(timestamp) {
            if (timestamp !== lastFrame) {
              if (lastFrame !== null) maximumFrameGap = Math.max(maximumFrameGap, timestamp - lastFrame);
              lastFrame = timestamp;
              frames++;
            }
            callback.call(this, timestamp);
          });
          document.addEventListener('visibilitychange', () => record('visibility', { hidden: document.hidden }));
          window.milligramStartupSnapshot = () => {
            const box = document.querySelector('#agent-panel .xterm-rows')?.getBoundingClientRect();
            return { summary: { ms: now(), firstOpen, firstReceipt, firstParse, firstGreeting, receivedBytes, parsedBytes,
              frames, maximumFrameGap: Math.round(maximumFrameGap), lastFrameAge: lastFrame === null ? null : Math.round(performance.now() - lastFrame),
              hidden: document.hidden, focused: document.hasFocus(), rendered: rendered(),
              width: Math.round(box?.width ?? 0), height: Math.round(box?.height ?? 0), dropped },
            events: [...events]
            };
          };
        })();
        """;
}
