# Milligram

[![CI](https://github.com/jhnoor/milligram/actions/workflows/ci.yml/badge.svg)](https://github.com/jhnoor/milligram/actions/workflows/ci.yml)

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
- For the agent: `tmux` and [GitHub Copilot CLI](https://github.com/github/copilot-cli)
- For coverage: test projects that reference `coverlet.collector` (the default in `dotnet new xunit`)

`milligram doctor` checks all of these for your project, plus whether it is restored, and prints the
fix for anything missing. The first run checks them too.

## Install

```bash
git clone <this repo> && cd milligram
dotnet pack src/Milligram -c Release
dotnet tool install -g Milligram --add-source ./artifacts
```

## Use

```bash
cd /path/to/your/csharp/project
milligram
```

The first run writes `milligram.json` from the namespaces it finds. It also infers the
**levels** from the dependencies: a namespace that depends on nothing is innermost, and each of
the others sits just outside the namespaces it uses. When namespaces depend on each other in a
cycle, Milligram breaks the cycle at its lightest link (the fewest references) and leaves that
link pointing outward, so the first diagram already shows the tangles in red. The console lists
the levels it chose. Edit `levels` to match the architecture you intend. Every run scans the code,
serves the viewer at `http://localhost:5170/` (loopback only), opens your browser, and starts the
agent in a terminal. Edit code or `milligram.json` and the diagram updates by itself.

To get metrics, run `milligram crap` (tests with coverage) and `milligram mutate` (Stryker), or use
the buttons in the viewer.

### In the viewer

| Do this | To |
|---------|----|
| Double-click a component | Open it. **Esc**, **←**, or the breadcrumbs go back up. |
| Double-click a type | Open its card: every member with CRAP, complexity, coverage, and mutants, plus what it uses and what uses it. Click a member to read its source. |
| Hover an arrow | List the type-level dependencies it bundles. Red ones break the rule. |
| Right-click a box | Refresh CRAP or mutation for it, omit it, or ask the agent about it. |
| Type in **Agent** and press Ctrl+Enter | Send a message to the agent, together with the diagram and your selection. |
| Pick a diagram under **Diagrams** | Switch between the real diagram and proposals. |

- **Arrows:** Detailed (between nested boxes), Bundled (between components), or Hidden (triangles show incoming/outgoing).
- **Boxes:** Members, Names, or Closed.
- **Navigation:** drag or scroll to pan, Ctrl+wheel to zoom, **F** to fit, **R** to reload.
- **Levels:** each box shows its level (`L0` is innermost, drawn at the bottom).
- **Colour:** red → green is the average of the CRAP and mutation grades. The **C** and **M** dots show each grade, and grey means unknown.

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
| `agent` | `enabled`, `command`, `allowTools`, `model`, `terminal` (`"auto"`, `"none"`, or a command using `{session}`), `keepOnExit`. |
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
- **Mutation**: Stryker.NET runs on the files you pick. Later runs only mutate methods whose code
  changed. After improving *tests*, use `--all` (or **Refresh all mutation**) to re-measure.
  Survived or uncovered mutants are test gaps.

A component takes the worst grade of anything inside it. Values dim on the card when the code has
changed since they were measured.

## The agent

`milligram` starts Copilot CLI in a tmux session for the project, and opens a terminal window on it
(Windows Terminal under WSL, Terminal on macOS). If no window appears, run `milligram agent attach`.
The agent's instructions are in `.milligram/agent.md`, and each project keeps one conversation,
resumed on every start.

By default the agent may run `milligram` commands and edit `milligram.json` without asking.
Anything else, such as editing code, asks for your approval in its terminal. Add patterns to
`agent.allowTools` to allow more.

The viewer and agent exchange JSON files in `.milligram/mail/`. The agent reads with `milligram mail`
and replies with `milligram tell`. The viewer runs scans, metrics, and omits itself, so those cost no
agent tokens.

## Commands

| Command | Does |
|---------|------|
| `milligram` | Viewer and agent. Options: `--port N`, `--no-agent`, `--no-browser`, `--keep-agent`, `--project DIR`. |
| `milligram init [--force]` | Write `milligram.json` from the source, inferring levels from the dependencies. |
| `milligram ir` | Rescan the source. |
| `milligram crap [--coverage file.xml]` | Run the tests with coverage (or read a Cobertura file) and score CRAP. |
| `milligram mutate [--all] [files…]` | Mutation-test changed methods (every file if you list none). |
| `milligram doctor` | Check the SDK, restore, test projects, coverage collector, Stryker, and the agent; print the fix for anything missing. Exits 1 if something is. |
| `milligram mail [--peek]` | Print and remove mail for the agent. |
| `milligram tell display <real\|proposalId>` / `tell notify "text"` | Send mail to the viewer. |
| `milligram agent status\|start\|stop\|attach` | Manage the agent session. |

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
