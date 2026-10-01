# Milligram

[![CI](https://github.com/jhnoor/milligram/actions/workflows/ci.yml/badge.svg)](https://github.com/jhnoor/milligram/actions/workflows/ci.yml) · [Website](https://jhnoor.github.io/milligram/)

A live architecture viewer for C# codebases, with an AI agent at your side.

Milligram draws your code as a component diagram, with namespaces as components and types
inside them. You can click from the top level all the way down to a method's source. Boxes are
coloured by **CRAP** (complexity × missing test coverage) and **mutation score**. Arrows that break
the **dependency rule** (an inner layer depending on an outer one) are drawn red.

A companion agent (GitHub Copilot CLI) runs next to the diagram. Point at a box, say what bothers
you, and the agent explains it or changes the code. Describe a **proposal** (a what-if regrouping
of your namespaces) and the agent draws it. Refine it together, then have the agent make the code
match.

*Inspired by Robert C. Martin's [uml-viewer](https://github.com/unclebob/uml-viewer). Independent C# implementation.*

## Requirements

- .NET 10 SDK
- For mutation scores: [Stryker.NET](https://stryker-mutator.io/docs/stryker-net/introduction/) (`dotnet tool install -g dotnet-stryker`)
- For the default agent host: `tmux` and [GitHub Copilot CLI](https://github.com/github/copilot-cli)
- For coverage: test projects that reference `coverlet.collector` (the default in `dotnet new xunit`)

`milligram doctor` checks all of these for your project, plus whether it is restored, and prints the
fix for anything missing. The first run checks them too.

The viewer, the editor integration, `crap` and `mutate` work the same everywhere. The agent differs:

| Platform | Agent |
|----------|-------|
| Linux | tmux, and a terminal emulator for its window (`x-terminal-emulator`, `gnome-terminal`, `konsole` or `xterm`). |
| macOS | tmux from Homebrew (`brew install tmux`). The agent opens in Terminal. |
| WSL2 (recommended on Windows) | tmux inside WSL. The agent opens in a Windows Terminal tab. On a Windows drive (`/mnt/c/…`), Linux isn't told about changes that Windows programs make, so Milligram also watches the project from Windows through `powershell.exe` (or polls, if WSL can't start Windows programs). A project in the Linux file system (for example `~/src`) scans faster and needs neither. |
| Windows | The default tmux host is unavailable ([#14](https://github.com/jhnoor/milligram/issues/14)); the experimental native host provides a browser panel and `milligram agent attach` in your terminal. You can also [run your own](#run-the-agent-yourself). Install PowerShell 7 for Copilot CLI (`winget install Microsoft.PowerShell`). |

### Older .NET Framework projects

The source diagram can be useful, but it is incomplete for these projects. The scanner binds
against .NET 10, `obj/project.assets.json` and explicit assembly `HintPath` entries in `.csproj`
files. Restored `packages.config` libraries bind when these entries point to their DLLs. Missing
or invalid assemblies are reported during scans; conditional or property-based paths are reported
as needing MSBuild evaluation. The scanner does not resolve .NET Framework reference assemblies.
It also reads all `.cs` files under `src` without evaluating
`Compile` items or build conditions. Use `exclude` for inactive files. `milligram doctor` flags
these limitations and recognizes test frameworks listed in `packages.config`.

Automatic coverage requires SDK-style test projects with `coverlet.collector`. SDK-style projects
targeting .NET Framework still need a compatible Windows test environment. For old-style test
projects, collect a Cobertura report with your existing tools and import it using
`milligram crap --coverage report.xml`. Framework Stryker runs need a solution path in the
source project's `stryker-config.json`, plus working MSBuild and NuGet tools. Stryker discovers
MSBuild automatically; its `--msbuild-path` override is a CLI option that Milligram does not
currently forward. See [Stryker's setup](https://stryker-mutator.io/docs/stryker-net/getting-started/)
and [configuration](https://stryker-mutator.io/docs/stryker-net/configuration/).

The [legacy investigation](LEGACY.md) measures recovered library edges and extra scanned files
on an actual MVC 4 / .NET Framework 4.5 application. Building it, collecting coverage and running
mutation remain unverified; [#27](https://github.com/jhnoor/milligram/issues/27) tracks that work.

## Install

```bash
git clone https://github.com/jhnoor/milligram && cd milligram
dotnet pack src/Milligram -c Release
dotnet tool install -g Milligram --source ./artifacts --prerelease
```

Local builds use version `0.0.0-dev`. The release workflow takes its version from a `v*` tag,
tests the package on Linux, macOS and Windows, then publishes it. See [Releasing](RELEASING.md)
for the one-time NuGet setup. Until the first stable release is published, use the source install
above. After that, `dnx Milligram` will run it directly, or `dotnet tool install -g Milligram`
will put it on your PATH.

## Use

```bash
cd /path/to/your/csharp/project
milligram
```

The first run reports parsing, binding and dependency progress while it scans, then writes
`milligram.json` from the namespaces it finds. It also infers the
**levels** from the dependencies: a namespace that depends on nothing is innermost, and each of
the others sits just outside the namespaces it uses. When namespaces depend on each other in a
cycle, Milligram breaks the cycle at its lightest link (the fewest references) and leaves that
link pointing outward, so the first diagram already shows the tangles in red. The console lists
the levels it chose. Edit `levels` to match the architecture you intend. The first run reuses that
analysis for its diagram after checking source and project inputs; edits during initialization
or inputs that cannot be verified trigger a fresh scan. Later starts scan the current code. Milligram
serves the viewer at `http://localhost:5170/` (loopback only), opens your browser, and starts the
agent in a terminal. Edit code, a `.csproj` or `milligram.json` and the diagram updates by itself.
Restored `obj/project.assets.json` files and generated global usings also trigger a new scan;
ordinary build output is ignored.

Source discovery, project discovery and polling skip nested directory symlinks and Windows
junctions, so a link back to a parent cannot trap a scan in a loop. Point `src` at the actual
source directory when code lives behind such a link; an explicitly selected linked root works too.
Source preview and editor requests also reject nested links that lead outside the project root.
Links between files or directories inside that root remain usable.

Opening a project through a directory symlink or Windows junction reuses the same agent as
opening its physical directory. Agent names resolve aliases in the project root and its ancestors.

Initialization also selects up to eight external libraries for the `foreign` ovals, ranked by
how many of your types use them. It groups namespaces at two segments (three for `System.*`),
keeps choices such as `System.Text.Json`, and leaves out routine collections, LINQ, threading,
compiler services and nullability annotations. These are namespace names, not package ids.
Restore your projects first so package references bind; edit `foreign` to choose what matters.

The default scanner uses one source compilation rather than evaluating MSBuild. It reads generated global
usings from the latest build, or falls back to explicit, unconditional `ImplicitUsings` settings
in project XML (`enable` or `true`). Imported and conditional settings and stale generated files
can therefore differ from the actual build; project-input evaluation remains a known limit.

For an **experimental, explicit project scan**, run:

```bash
milligram ir --msbuild src/App/App.csproj --configuration Release
```

Repeat `--msbuild` for additional entry projects. Paths are relative to the examined root
(`--project DIR`); the selected projects and their project references replace `src` selection.
Policy `exclude` patterns still apply to the diagram. Linked files inside the root keep their
physical source locations, including files outside `src`. Files outside the root and generated
sources without a physical path contribute to binding but do not get source cards.

This mode uses MSBuild/Roslyn compiler inputs, including conditional `Compile` items, imported
properties, preprocessor symbols and fresh SDK global usings. Each project keeps its own imports
and reference assemblies. Repeated type names get project-qualified IDs when needed. Install the
project's SDK and reference packs and restore its packages first; missing references produce
diagnostics and incomplete edges. A project that fails normal MSBuild evaluation leaves the old
model intact. Run plain `milligram ir` to explicitly choose the approximate source-only fallback.
The configuration defaults to the project's choice when omitted.

On Windows, old-style projects also need a short tool installation path: Roslyn's .NET Framework
host can time out when its `.exe.config` path reaches 260 characters. The command diagnoses deep
installations; use a shorter `dotnet tool install ... --tool-path DIR` when this occurs.

Project evaluation runs design-time targets and source generators, which can execute project tasks
and write `obj` files. This is a manual `ir` option: startup, initialization, the live watcher and
`doctor` still use the default source-only path. A later ordinary scan replaces the evaluated
snapshot. Do not run this experiment alongside a live viewer of the same project. Automatic
project selection, watcher invalidation and large project-graph measurements remain
[#37](https://github.com/jhnoor/milligram/issues/37); this option does not complete that issue.
The [project-scan checks](PROJECT_SCANNING.md) describe the automated fixtures and remaining work.

File-local types keep separate identities and source cards even when their names match. Other
repeated source type names are kept distinct when Roslyn provides separate symbols, and the scan
reports them. Their references can still be ambiguous in the source-only compilation; resolving
each project's inputs remains [#37](https://github.com/jhnoor/milligram/issues/37). Previously
measured types whose identities collided need fresh coverage and mutation measurements.

The [large source-tree check](SCALING.md) records a scan of 18,110 files and 6.5 million physical
C# lines, including its runtime, memory use and remaining project-binding limits.

To get metrics, run `milligram crap` (tests with coverage) and `milligram mutate` (Stryker), or use
the buttons in the viewer.

### In the viewer

| Do this | To |
|---------|----|
| Double-click a component | Open it. **Esc**, **←**, or the breadcrumbs go back up. |
| Double-click a type | Open its card: every member with CRAP, complexity, coverage, and mutants, plus what it uses and what uses it. Click a member to read its source. |
| Use **Browse this level** | Filter namespace and type names on the current level, or page through a large view. Open a namespace to browse the next level. **Show all** clears the filter. |
| Hover an arrow | List the type-level dependencies it bundles. Red ones break the rule. |
| Right-click a box | Refresh CRAP or mutation for it, omit it, or ask the agent about it. |
| Type in **Agent** and press Ctrl+Enter | Send a message to the agent, together with the diagram and your selection. |
| Pick a diagram under **Diagrams** | Switch between the real diagram and proposals. |

- **Arrows:** Auto (the default: a compact grid for large views, bundled arrows for small ones), Detailed (between nested boxes), Bundled (between components), or Hidden. Triangles in the grid and Hidden mode show incoming/outgoing references; hover for the dependencies.
- **Boxes:** Members, Names, or Closed.
- **Navigation:** drag or scroll to pan, Ctrl+wheel to zoom, **F** to fit, **R** to reload.
- **Levels:** each box shows its level (`L0` is innermost, drawn at the bottom in layouts with arrows). The compact grid keeps the level labels.
- **Colour:** red → green is the average of the CRAP and mutation grades. The **C** and **M** dots show each grade, and grey means unknown.

Views that would expand beyond 80 interior boxes open as a compact overview, with up to 20
namespaces or types per page. This also handles types directly under the root. Dotted summary
boxes represent entries on other pages or outside the current focus; they are navigation aids,
not additional namespaces. Their colours retain the worst grades of their contents. References
between entries remain in the arrows or markers, and collapsed internal reference counts and
level violations appear on the box. Open a namespace or type card to inspect the details.
The exterior is bounded to 12 boxes, including its summary. Paging and filtering do not edit
`milligram.json`, omit code, or change levels. Cameras are remembered separately for each
diagram, focus, detail setting, overview layout and filtered page.

## Configure: `milligram.json`

Comments are allowed. Paths are relative to `prefix` and match whole dotted segments. `milligram init`
writes a short, commented starter. When the viewer edits the file (omit, proposals), it rewrites only
the keys it changes, so your comments and layout stay.

```jsonc
{
  "src": "src",
  "exclude": ["**/bin/**", "**/obj/**", "tests/**"],
  "prefix": "Shop",                                   // Shop.Domain.Order -> Domain.Order
  "order": ["Web", "Services", "Domain"],             // box order
  "levels": [["Domain"], ["Services"], ["Web"]],      // inner (0) first; drives red arrows
  "foreign": ["Microsoft.EntityFrameworkCore"],       // libraries to draw as ovals
  "omit": ["Legacy"],                                 // hide namespaces or types
  "edgeKinds": [{ "from": "Web", "to": "Domain.Order", "kind": "association" }],  // exempt from the rule
  "proposals": []
}
```

Optional keys:

| Key | Purpose |
|-----|---------|
| `omitEdges` | Leave specific dependencies out of the drawing. |
| `tests` | `{ "projects": [...], "filter": "..." }` — which test projects to run. The default is every detected test project. |
| `thresholds` | `crapGood` / `crapBad` (5 / 30) and `mutationGood` / `mutationBad` (0.9 / 0.5). |
| `agent` | `enabled`, `host` (`"tmux"` by default; experimental `"milligram"`), `command`, `args`, `allowTools`, `model`, `terminal` (`"auto"`, `"none"`, or a command using `{session}` for tmux or `{command}` for native attach), `keepOnExit`. |
| `editor` | Command for **Open in editor**. The default is `code -g {file}:{line}`. |

The real diagram *is* your namespace tree. Milligram never invents components. For a grouping that
isn't in the code, use a proposal.

### Proposals

A proposal regroups existing namespaces (or single types) into named layers. Layers are listed
inner first, and every arrow is judged again against that order. Anything not listed shows under
*Unassigned*.

```json
"proposals": [{
  "id": "hexagonal",
  "name": "Hexagonal",
  "layers": [
    { "id": "core",     "label": "Core",     "namespaces": ["Domain", "Services"] },
    { "id": "adapters", "label": "Adapters", "namespaces": ["Web", { "id": "io", "label": "IO", "namespaces": ["Files"] }] }
  ],
  "omit": ["Legacy"]
}]
```

You can write proposals by hand, but it's usually easier to describe one to the agent. **+ New
proposal** in the viewer creates an empty one, and right-clicking a proposal renames or deletes it.

## Metrics

Snapshots live in the examined project's `.milligram/metrics/`. `milligram init` git-ignores
`.milligram/`, because everything in it can be regenerated. If you want clones or teammates to see
the numbers without re-running the tests, remove that line and commit `.milligram/metrics/`.
Snapshots are written in sorted order, so diffs stay small.

- **CRAP** = complexity² × (1 − coverage)³ + complexity, for each method. Complexity comes from
  Roslyn and line coverage from coverlet.
- **Mutation**: Stryker.NET runs on the files you pick. Later runs only mutate members whose code
  changed, including field and property initializers and explicit enum values. After improving
  *tests*, use `--all` (or **Refresh all mutation**) to re-measure whole files, including attributes.
  Survived or uncovered mutants are test gaps. Open a type card's **Test gaps** to see each
  mutation's replacement code and jump to its source. Older snapshots keep their counts;
  refresh all mutation to add the details.

If Stryker fails before producing a report, check the build/test output and run `milligram doctor`.
When dogfooding Milligram on Windows, use the installed tool or publish a separate copy first:

```bash
dotnet publish src/Milligram -o .milligram/dogfood
dotnet .milligram/dogfood/Milligram.dll crap
dotnet .milligram/dogfood/Milligram.dll mutate
```

Running from `dotnet run` holds the same binaries that coverage and Stryker need to rebuild.

A component takes the worst grade of anything inside it. Values dim on the card when the code has
changed since they were measured. Adding an unmeasured method also marks the type's existing
summary stale. A new type stays unknown even if another type in its file was already mutation-tested.

## The agent

By default, `milligram` starts Copilot CLI in a tmux session for the project, and opens a terminal window on it
(Windows Terminal under WSL, Terminal on macOS). If no window appears, run `milligram agent attach`.
The agent's instructions are in `.milligram/agent.md`, and each project keeps one conversation,
resumed on every start.

By default the agent may run `milligram` commands and edit `milligram.json` without asking.
Anything else, such as editing code, asks for your approval in its terminal. Add patterns to
`agent.allowTools` to allow more.

The viewer and agent exchange JSON files in `.milligram/mail/`. The agent reads with `milligram mail`
and replies with `milligram tell`. The viewer runs scans, metrics, and omits itself, so those cost no
agent tokens.

### Run the agent yourself

With `--no-agent`, or where the selected host's prerequisites are missing, run the agent in a
terminal of your own. Milligram writes `.milligram/agent.md` every time it starts, and the banner says
so. In the project folder:

```bash
copilot --allow-tool 'shell(milligram:*)'    # then: "Read .milligram/agent.md and follow it."
```

Without `--allow-tool`, Copilot asks before each `milligram` command. The agent needs `milligram` on
its PATH, as it is once installed as a tool. There is no doorbell: after you send something from the
viewer, tell the agent to run `milligram mail`.

## Commands

| Command | Does |
|---------|------|
| `milligram` | Viewer and agent. Options: `--port N` (1–65535, default 5170; tries up to 29 following ports), `--no-agent`, `--no-browser`, `--keep-agent`, `--project DIR`. |
| `milligram init [--force]` | Write `milligram.json` from the source, inferring levels from the dependencies. |
| `milligram ir [--msbuild FILE.csproj] [--configuration NAME]` | Rescan the source. Experimental `--msbuild` evaluates the selected project and its references; repeat it for more entry projects. |
| `milligram crap [--coverage file.xml]` | Run the tests with coverage (or read a Cobertura file) and score CRAP. |
| `milligram mutate [--all] [files…]` | Mutation-test changed members, including initializers (every file if you list none). |
| `milligram doctor` | Check the SDK, restore, test projects, coverage collector, Stryker, and the agent; print the fix for anything missing. Exits 1 if something is. |
| `milligram mail [--peek]` | Print and remove mail for the agent. |
| `milligram tell display <real\|proposalId>` / `tell notify "text"` | Send mail to the viewer. |
| `milligram agent status\|start\|stop\|attach` | Manage the session selected by `agent.host`. With an unreadable policy, `stop` attempts both project backends. Native `attach` uses the current terminal; Ctrl+] then d detaches. |

The native host is under development in [AGENT_HOST.md](AGENT_HOST.md). To try it, set
`"agent": { "host": "milligram" }`: `serve`, `agent start`, `agent status`, `agent stop` and
`agent attach` use the detached native host. A terminal panel opens below the diagram when the
agent is running. Native support
targets Linux with glibc, macOS and Windows 10 version 1809 or later, each on x64 or ARM64.
`doctor` checks its native library and, on Windows, `pwsh`. Copilot needs PowerShell 6 or later;
PowerShell 7 is the recommended install.

Native `agent attach` relays the current terminal, including Ctrl+C and resizes. Press **Ctrl+]**,
then **d** to detach while leaving the agent running. Press Ctrl+] twice to send a literal Ctrl+].
You can reattach or attach another client to the same session. Input and output must be terminals,
so use `ssh -t` for an SSH connection. The command returns the agent's exit code when it exits.
Terminal modes are restored on detach, connection failure, and handled termination signals;
forced process termination such as SIGKILL cannot run cleanup.

The browser panel has Start, Stop, Restart and Pop out controls. Pop out opens `/agent.html` on
the same session; closing or collapsing a panel detaches it without stopping the agent. Drag the
divider, or focus it and use the arrow keys, to resize it. Height, collapse and screen-reader
preferences are remembered in the browser. `agent.terminal: "auto"` opens the panel automatically;
`"none"` keeps it collapsed until you open it.

For an external native terminal, set `agent.terminal` to a command ending in a separate
`{command}` argument, for example:

| Terminal | `agent.terminal` |
|----------|------------------|
| Windows Terminal | `"wt.exe new-tab {command}"` |
| GNOME Terminal | `"gnome-terminal -- {command}"` |
| WezTerm | `"wezterm start -- {command}"` |

The template runs when Milligram starts a new host; reusing an existing host does not open
another window. `{command}` expands to the same build's `agent attach --project DIR` as separate
arguments, preserving paths with spaces. Double quotes group template arguments. Choose a
terminal that accepts an executable and its arguments, rather than a shell command string.
Milligram escapes generated semicolons for Windows Terminal's command parser. `doctor` reports
invalid templates or a missing terminal executable. If the window cannot start, use
`milligram agent attach` in your terminal. That command always attaches in the current terminal.
Custom templates leave the viewer panel collapsed until you open it.

With the tmux host, custom commands use the raw `{session}` name. Use tmux's exact selector,
for example `"gnome-terminal -- tmux attach -t ={session}"`, so it cannot select another
session whose name starts the same way. Built-in attachment and session controls use exact matches.

**Ctrl+backtick** focuses or collapses the panel. Terminal keys stay out of the diagram's shortcuts.
**Ctrl+C** copies a selection, or interrupts when nothing is selected. Paste uses the terminal's
bracketed-paste mode when the agent enables it. **Ctrl/Cmd+click** on a C# `file:line` or `file(line)`
opens the source viewer (the configured editor in the popout); paths must remain inside the project.
The Screen reader checkbox enables xterm's accessible output. Browser-reserved shortcuts, such as
Ctrl+L and Ctrl+W, remain browser shortcuts; use `milligram agent attach` for a full terminal window.
After a connection loss the panel retries with bounded backoff and replays recent output. A hidden
browser tab detaches until visible again. Replay is bounded history, so it may not reconstruct an
old full-screen terminal display exactly.

Stop the old session before changing `agent.host`, then restart the viewer. Each viewer keeps
its initial backend so a policy reload cannot redirect its shutdown command. On exit, a viewer
stops only the session it originally started. A replacement started later, including through
the Restart button, stays running. The Stop button and `agent stop` stop the current session.
If `milligram.json` is unreadable, `agent stop` attempts both backends for this project; other
agent commands still require a valid policy.
`--keep-agent` and `agent.keepOnExit` leave the original session running too.
The internal `agent host --project DIR [--instance ID]` entry point is for the detached launcher
and integration fixtures; the launcher supplies the identity used for automatic cleanup.

## Develop

```bash
dotnet tool restore                                        # Stryker, for this repo
dotnet test                                                # includes a check that Milligram obeys its own dependency rule
dotnet format Milligram.slnx --verify-no-changes           # lint
dotnet run --project src/Milligram -- serve --no-agent     # view Milligram itself
```

Contributor and agent guidance (layout, conventions, what must change together) is in
[AGENTS.md](AGENTS.md).

Milligram's own layers, as set in its `milligram.json`:

- `Domain` (0) — pure model and rules.
- `Application` (1) — use cases and ports.
- `Analysis` and `Adapters` (2) — Roslyn, readers, web, tmux.
- `Main` (3) — composition.

The diagram layout uses [ELK](https://github.com/kieler/elkjs), vendored under `wwwroot/lib`
(EPL-2.0).
