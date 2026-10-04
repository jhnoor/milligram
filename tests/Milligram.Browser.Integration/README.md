# Browser integration fixture

This console test project is deliberately outside `Milligram.slnx` and the fast xunit suite.
It starts a real loopback viewer and a deterministic terminal peer behind the production
host runtime, session, same-user pipe and WebSocket relay. It needs no Copilot login or tmux.
The native terminal adapter has its own real-process fixture in `Milligram.AgentHost.Integration`.

```powershell
dotnet build tests/Milligram.Browser.Integration -c Release -warnaserror
pwsh tests/Milligram.Browser.Integration/bin/Release/net10.0/playwright.ps1 install --with-deps chromium firefox webkit
dotnet run --project tests/Milligram.Browser.Integration -c Release --no-build -- chromium
dotnet run --project tests/Milligram.Browser.Integration -c Release --no-build -- firefox
dotnet run --project tests/Milligram.Browser.Integration -c Release --no-build -- webkit
```

Use `chrome` or `msedge` to test installed branded browsers. `--serve` keeps the fixture viewer
open for manual inspection until Ctrl+C; publish it to an isolated directory before rebuilding
on Windows. Screenshots go to ignored `artifacts/browser/`. CI runs Chromium and Firefox on
Linux, WebKit on macOS, and Chrome and Edge on Windows. WebKit coverage is not Safari acceptance.

The checks cover Unicode and ANSI output, input echo, keyboard isolation, interrupt, the
bracketed-paste handler (a synthetic event leaves the system clipboard untouched), inspector
mail and the doorbell, resize and remembered height, detach/replay, a burst beyond the output
credit window, source links, shared popout, stop/start/restart, accessible output, policy-driven
auto-open, literal untrusted text, and refusal to reconnect when a port serves another project.
Wrapped source-link cases use the actual xterm buffer, including wide glyphs and paths with spaces.
Real Copilot interaction, IME, operating-system clipboard shortcuts and Safari remain manual
acceptance checks.

`chrome --startup` repeats the initial terminal assertion in 16 fresh browser contexts at normal
speed, then another 16 with Chrome's renderer CPU throttle set to 4x. Chromium and Edge support
the same mode; Firefox and WebKit run only the normal-speed rounds. Each round keeps the original
15-second assertion and stops at the first failure, without retrying. CI runs this mode on Chrome.
Renderer throttling does not reproduce host/server scheduling pressure or every real-machine load.

Terminal runs write bounded startup diagnostics under `artifacts/browser/`: connection, binary
receipt, xterm parse acknowledgements, resize dimensions, DOM greeting observation and callbacks
already requested through `requestAnimationFrame`. The probe does not schedule extra animation
frames or force layout from its DOM observer. Browser/runtime/OS versions are printed separately.
Callback gaps can include idle time with no requested work; they are not proof of a blocked renderer.
On failure, snapshots before and after the screenshot distinguish assertion-time state from later
state. Numeric counts and the presence of the fixed fixture greeting are retained; terminal text,
WebSocket addresses and secret subprotocols are not. A snapshot has at most 256 events, with a
dropped-event count; a stalled page gets a bounded three-second diagnostic attempt.

These probes support [#55](https://github.com/jhnoor/milligram/issues/55). Passing repetitions do
not explain the historical intermittent failure, and observing the DOM does not establish when
the compositor painted it. No product synchronization fix or longer startup deadline is implied.

`chrome --diagram` selects a generated 620-type diagram fixture instead of the terminal checks.
It verifies a model arriving over SSE after an empty first screen, automatic fit in a smaller
viewport, bounded pages, exact reference/violation totals, namespace and name navigation,
cards/source, and camera restoration through paging, filtering, browser history and proposals.
It prints first-layout and interaction times. CI runs this fixture on every browser above.

`chrome --diagram --cached-project /absolute/project/path` reads an existing `.milligram/model.json`
without rescanning. The acceptance checks expect the pinned Roslyn input in `SCALING.md`,
including its repeated Razor delegate identities. `--diagram --serve` generates the wide
fixture and leaves its viewer open; add `--cached-project` to inspect a saved model instead.
