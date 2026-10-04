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

The internal pilot prioritizes Windows and Linux. macOS is experimental pending validation on a
physical Mac; its CI checks remain enabled. The agent setup differs by platform:

| Platform | Agent |
|----------|-------|
| Linux | tmux, and a terminal emulator for its window (`x-terminal-emulator`, `gnome-terminal`, `konsole` or `xterm`). |
| macOS | tmux from Homebrew (`brew install tmux`). The agent opens in Terminal. |
| WSL2 (recommended on Windows) | tmux inside WSL. The agent opens in a Windows Terminal tab. On a Windows drive (`/mnt/c/…`), Linux isn't told about changes that Windows programs make, so Milligram also watches the project from Windows through `powershell.exe` (or polls, if WSL can't start Windows programs). A project in the Linux file system (for example `~/src`) scans faster and needs neither. |
| Windows | The default tmux host is unavailable ([#14](https://github.com/jhnoor/milligram/issues/14)); the experimental native host provides a browser panel and `milligram agent attach` in your terminal. You can also [run your own](#run-the-agent-yourself). Install PowerShell 7 for Copilot CLI (`winget install Microsoft.PowerShell`). |

### Older .NET Framework projects

Project scans evaluate `Compile` items, configuration, imports and framework references with
MSBuild. Install the appropriate SDK, .NET Framework targeting packs and restored packages first.
Microsoft also provides [reference assemblies through NuGet](https://learn.microsoft.com/en-us/dotnet/framework/migration-guide/reference-assemblies)
when the developer pack is unavailable. Legacy ASP.NET projects additionally need their web build
targets: `VSToolsPath` must resolve to the tools directory containing `WebApplications`.
Missing project imports fail evaluation with a diagnostic.
On Windows, legacy projects require a short enough tool installation path for the .NET Framework
build host. If its configuration path reaches 260 characters, Milligram stops before launching it
and recommends a shorter `--tool-path`; modern SDK scans can still use that installation.
Missing assemblies are diagnosed; source membership can still be useful while bindings are
incomplete. Full legacy build, coverage and mutation acceptance remains unverified.
The explicit `--source-only` fallback binds against .NET 10, restored assets and direct `HintPath`
entries, and reads all `.cs` files under `src` without evaluating conditions. Its diagram is an
approximation; use `exclude` for inactive files. `doctor` explains the selected scan mode.

Automatic coverage requires SDK-style test projects with `coverlet.collector`. SDK-style projects
targeting .NET Framework still need a compatible Windows test environment. For old-style test
projects, collect a Cobertura report with your existing tools and import it using
`milligram crap --coverage report.xml`. Framework Stryker runs need a solution path in the
source project's `stryker-config.json`, plus working MSBuild and NuGet tools. Stryker discovers
MSBuild automatically; its `--msbuild-path` override is a CLI option that Milligram does not
currently forward. See [Stryker's setup](https://stryker-mutator.io/docs/stryker-net/getting-started/)
and [configuration](https://stryker-mutator.io/docs/stryker-net/configuration/).

The [legacy investigation](LEGACY.md) records a prepared MVC 4 / .NET Framework 4.5 application:
27 evaluated source files, 32 types and 98 dependencies, with the unlisted-source probe excluded.
Automated fixtures also check mixed net48/net10.0 contexts and conditional references; the scan
evidence is tracked in [#36](https://github.com/jhnoor/milligram/issues/36). Building the legacy
application, collecting its coverage and running mutation remain unverified;
[#38](https://github.com/jhnoor/milligram/issues/38) and [#39](https://github.com/jhnoor/milligram/issues/39)
track those acceptance checks.

## Install

For an internal pilot, download a tested preview package from a green CI run and run it with
the .NET 10 SDK. [Preview installation and rollback](PREVIEW.md) require no local build and
leave an existing global Milligram installation alone. GitHub sign-in is required to download
the artifact; previews expire after 30 days.

To build from source:

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

Source-only discovery, project discovery and polling skip nested directory symlinks and Windows
junctions, so a link back to a parent cannot trap a scan in a loop. Point `src` at the actual
source directory when code lives behind such a link; an explicitly selected linked root works too.
Evaluated source membership follows the project's MSBuild inputs.
Source preview and editor requests also reject nested links that lead outside the project root.
Links between files or directories inside that root remain usable.

Opening a project through a directory symlink or Windows junction reuses the same agent as
opening its physical directory. Agent names resolve aliases in the project root and its ancestors.

Initialization also selects up to eight external libraries for the `foreign` ovals, ranked by
how many of your types use them. It groups namespaces at two segments (three for `System.*`),
keeps choices such as `System.Text.Json`, and leaves out routine collections, LINQ, threading,
compiler services and nullability annotations. These are namespace names, not package ids.
Restore your projects first so package references bind; edit `foreign` to choose what matters.

By default, Milligram discovers C# projects under `src` and in its ancestor directories up to the
examined root, applying `exclude` to entry projects. It evaluates their compiler inputs with
MSBuild and follows project references. When no project is found it reports the approximate
source-only fallback. Evaluation failures preserve the previous diagram and explain how to fix
the project or select that fallback explicitly.

To select entry projects and a configuration, run:

```bash
milligram --msbuild src/App/App.csproj --configuration Release
```

Repeat `--msbuild` for additional entry projects. Paths are relative to the examined root
(`--project DIR`); the selected projects and their project references replace `src` selection.
Policy `exclude` patterns still apply to the diagram. Linked files inside the root keep their
physical source locations, including files outside `src`. Files outside the root and generated
sources without a physical path contribute to binding but do not get source cards.

This mode uses MSBuild/Roslyn compiler inputs, including conditional `Compile` items, imported
properties, preprocessor symbols and fresh SDK global usings. Each project keeps its own imports
and reference assemblies. Multi-target projects retain each evaluated framework context and its
matching project-reference graph; repeated type names get project-qualified IDs when needed. Install the
project's SDK and reference packs and restore its packages first; missing references produce
diagnostics and incomplete edges. A project that fails normal MSBuild evaluation leaves the old
model intact. Run `milligram ir --source-only` to explicitly choose the approximate source-only fallback.
The configuration defaults to the project's choice when omitted. These options also work with
`init` and `ir`. A first run saves them in `milligram.json`; with an existing policy they override
that invocation only. Set `scan.projects` and `scan.configuration` in the policy to persist changes.
Coverage and mutation commands also use the selected `scan.configuration`. Evaluated member
fingerprints include the configuration, project identity, compiler flags, symbols and referenced
assembly identities, so changing these makes old metrics stale and includes members in the next
differential mutation run. Imported coverage reports must come from the same configuration.
Coverage runs each evaluated test framework separately and attributes assembly reports only to
the library contexts that test run references. Mutation uses the owning project and framework,
including linked files outside the project directory. Unmeasured contexts remain unknown. An
imported Cobertura report cannot distinguish frameworks sharing an assembly name; those contexts
stay unknown with a diagnostic. Use automatic coverage collection to establish ownership. Stryker
skips a context when its test-framework selection could reference a different library context;
use a single-target test project in that case.

On Windows, old-style projects also need a short tool installation path: Roslyn's .NET Framework
host can time out when its `.exe.config` path reaches 260 characters. The command diagnoses deep
installations; use a shorter `dotnet tool install ... --tool-path DIR` when this occurs.

Project evaluation runs design-time targets and source generators, which can execute project tasks
and write `obj` files. Use `--source-only` or `"scan": { "mode": "sourceOnly" }` for an approximate
scan without executing these tasks. That fallback combines source into one compilation, uses
generated global usings from the latest build, and cannot reproduce project membership or conditions.

Startup, refresh, metrics-triggered scans and `ir` share the policy's scan settings. The viewer
also polls evaluated imports, linked sources, wildcard directories, additional generator inputs
and metadata references, including inputs outside the examined root. Polling starts at two seconds
and backs off when walking the inputs is expensive. Unrelated `bin`/`obj` output is ignored;
explicit evaluated inputs in those directories remain tracked. Edits to `scan` settings re-evaluate
the live diagram. A failed refresh retains the last good model and continues watching for repairs.
The [project-scan checks](PROJECT_SCANNING.md) describe the fixtures and remaining large-graph and
multi-target acceptance work in [#37](https://github.com/jhnoor/milligram/issues/37).

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
| `scan` | `mode`: `auto` (default), `msbuild` (require evaluated projects), or `sourceOnly` (approximate, no project tasks). `projects`: optional root-relative entry `.csproj` paths, replacing `src` selection; `configuration`: optional build configuration. |
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
| `milligram ir` | Rescan with the policy's project selection and configuration. |
| `milligram crap [--coverage file.xml]` | Run the tests with coverage (or read a Cobertura file) and score CRAP. |
| `milligram mutate [--all] [files…]` | Mutation-test changed members, including initializers (every file if you list none). |
| `milligram doctor` | Check the SDK, restore, test projects, coverage collector, Stryker, and the agent; print the fix for anything missing. Exits 1 if something is. |
| `milligram mail [--peek]` | Print and remove mail for the agent. |
| `milligram tell display <real\|proposalId>` / `tell notify "text"` | Send mail to the viewer. |
| `milligram agent status\|start\|stop\|attach` | Manage the session selected by `agent.host`. With an unreadable policy, `stop` attempts both project backends. Native `attach` uses the current terminal; Ctrl+] then d detaches. |

`milligram`, `init` and `ir` accept `--msbuild FILE.csproj` (repeatable), `--configuration NAME`,
or `--source-only`. First-run initialization saves this selection; later overrides apply only to
that invocation. Restart the viewer after changing the SDK selected by `global.json`; an existing
process cannot replace loaded MSBuild assemblies and reports this instead of publishing a stale scan.

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
