# AGENTS.md

Guidance for AI agents (and humans) working **on** Milligram itself. For what the tool does from a
user's point of view, read [README.md](README.md) first. It is short and accurate.

## What Milligram is for

Milligram gives you a map of a large codebase, meant for codebases of millions of lines. The map
shows where the risk is: high complexity, low coverage, weak tests, and dependencies that point the
wrong way. An agent working next to the map helps fix what it shows. The goal is a quick way to
decide what to work on next, and then to get it done with an agent.

Today it handles **C# only**. Roslyn scans the source into a model. The browser draws namespaces
as components, using an ELK layout. Boxes are coloured by CRAP (complexity × missing coverage) and
by Stryker.NET mutation score. A companion agent (Copilot CLI in tmux) talks to the viewer through
file mailboxes. Version 0.1.0.

## Current focus (decided September 2026)

- **C# / .NET only.** Don't add other languages or generalise for them yet.
- **The companion agent stays on Copilot CLI.** Don't add support for other agents yet.
- **Dogfood on this repo until the beta**, then roll out to the company's codebases for feedback.
- **The priority is onboarding**: any .NET repo, one command, a useful diagram. See the
  onboarding epic, [jhnoor/milligram#1](https://github.com/jhnoor/milligram/issues/1), and its
  sub-issues.

## Environment and commands

Requirements: the .NET 10 SDK. In Claude Code on the web, `.claude/hooks/session-start.sh`
installs it into `~/.dotnet` and restores packages and tools. Locally, install it yourself.
`tmux` and `copilot` are needed only for the companion agent, and Stryker only for mutation.

```bash
dotnet build                                   # must stay at 0 warnings
dotnet test                                    # whole xunit suite in ~10 s, including the architecture tests
dotnet test --filter "FullyQualifiedName~WorkspaceTests"   # one class
dotnet format Milligram.slnx --verify-no-changes           # lint gate: must pass before you commit
dotnet tool restore                            # Stryker 5, pinned in dotnet-tools.json

# Point Milligram at itself (milligram.json at the repo root describes its own layers):
dotnet run --project src/Milligram -- ir                     # rescan -> .milligram/model.json
dotnet run --project src/Milligram -- crap                   # tests + coverage -> .milligram/metrics/crap.json
dotnet run --project src/Milligram -- mutate src/Milligram/Domain/Metrics/Crap.cs   # differential mutation
dotnet run --project src/Milligram -- doctor                 # what the metrics and the agent need, with fixes
dotnet run --project src/Milligram -- --no-agent --no-browser   # viewer at http://localhost:5170/
```

Everything under `.milligram/` is regenerated and git-ignored. Never commit it.

## Layout

One project, `src/Milligram`, packed as the `milligram` dotnet tool. One test project,
`tests/Milligram.Tests`, whose folders mirror the source layers. Namespaces follow folders.

| Namespace | Level | Holds |
|-----------|-------|-------|
| `Domain` | 0 | Pure model and rules. `Model` (CodeModel, TypeNode, edges), `Policies` (milligram.json records, NamePath, Glob), `Hierarchy` (component tree, dependency rule, `Layering` used by `init` to infer levels), `Views` (what the browser draws), `Metrics` (CRAP, mutation, grades, snapshots), `Mail`. |
| `Application` | 1 | Use cases and ports. `Workspace` (current policy, model, and metrics, all thread-safe), `ViewerActions` (what the viewer's buttons do), `CrapService`, `MutationService`, `JobQueue`, `Mailbox`, `PolicyEditor`, `ProjectInitializer`, `AgentBriefing`, and `Ports.cs` (every interface an adapter implements). |
| `Analysis` | 2 | Readers of the outside world: the Roslyn `CSharp` scanner, the Cobertura and Stryker report readers, and the .csproj locator. |
| `Adapters` | 2 | Web server and SSE `EventHub`, tmux companion, file watcher, process runner (with `ProgramPath` and `BatchCommandLine` for Windows), CLI parsing. |
| `Main` | 3 | `Program` (commands) and `Composition` (the only place that constructs concrete classes). |
| `wwwroot/` | n/a | The viewer: vanilla JS with no build step (`app.js`, `index.html`, `style.css`), embedded in the assembly. `lib/elk.bundled.js` is vendored ELK (EPL-2.0). Do not edit it. |

Data flow: `CSharpScanner` → `CodeModel` (written to `.milligram/model.json`) → `TreeBuilder`
builds a `DiagramTree` for one context (`real`, or a proposal id) → `ViewBuilder` and
`TypeCardBuilder` apply the metrics → the JSON API (`/api/view`, `/api/type`, `/api/source`,
`/api/action`, `/api/events`) → `app.js` lays it out with ELK and draws SVG. `ProjectWatcher`
regenerates when `.cs` files, `milligram.json`, or metrics change, and pushes SSE events.

## Rules that tests enforce

- **The dependency rule.** `ArchitectureTests` scans this repo with its own `milligram.json` and
  fails on any edge from an inner level to an outer one. Domain → Application → Analysis/Adapters →
  Main: inner code never references outer code.
- **The Domain uses no frameworks.** Nothing under `Milligram.Domain` may reference
  `Microsoft.*`. System types are fine.
- A new top-level namespace has to be added to `levels` (and `order`) in `milligram.json`.
  Without a level it is **silently exempt** from the rule.
- Need something from an outer layer? Add an interface to `Application/Ports.cs`, implement it in
  `Analysis` or `Adapters`, and wire it up in `Main/Composition.cs`.

## Code conventions

Match the existing code. It is terse and consistent:

- File-scoped namespaces, primary constructors, `sealed` classes, and immutable `sealed record`s
  with `init` properties. Collection expressions (`[]`) everywhere. Nullable is on.
- Compare strings with `StringComparison.Ordinal` or `StringComparer.Ordinal`. Paths inside models
  are project-relative and use `/`. Convert with `ProjectPaths.Relative` and `ProjectPaths.Absolute`.
- Output must be deterministic: sort by ordinal id before writing (see `Workspace.SaveMetrics`,
  `DependencyCollector.Edges`). Snapshots should diff cleanly when rerun.
- Comments: a one-line `/// <summary>` on types and non-obvious members that says *why* or what
  the invariant is. Few other comments. No banners, no restating the code.
- All JSON goes through `MilligramJson.Options` and `JsonFile`. Web defaults, camelCase enums,
  comments and trailing commas allowed in input. `JsonFile.Write` is atomic (temp file + rename).
  Keep it that way, because the watcher and the agent read these files while they are being written.
  `milligram.json` is written through `PolicyText`: `init` writes a commented starter, and viewer edits
  (`Workspace.EditPolicy`) rewrite only the keys that changed, so hand-written comments survive. Never
  `JsonFile.Write` a whole `Policy` over it.
- For errors the user should see, throw `MilligramException`. `Program` and `ViewerActions` turn
  it into a message. Don't use it for bugs.
- Long work (scans, tests, Stryker) runs through `JobQueue`, one job at a time, and reports
  progress with the `log` callback.
- Keep the web server's safety guard: loopback host names only, POSTs must carry
  `X-Milligram: 1`, and file access must pass `ProjectPaths.Contains` (no path traversal).
- Start programs only through `ProcessRunner`. It resolves them with `ProgramPath` (PATHEXT on
  Windows), so lookup and launch agree, and runs `.cmd` and `.bat` files through `cmd.exe` with
  `BatchCommandLine`'s quoting, so a file name from the project can't run another command.

## Tests

- xunit, one test class per unit, in the folder of the layer it tests. Names are sentences:
  `MetricsSnapshotsAreWrittenInKeyOrder`.
- Build models by hand with `Build` (`Build.Type`, `Build.Edge`, `Build.LayeredApp()`). Use
  `TempProject` for throwaway directories. `Fakes.cs` has fakes for every port: processes,
  readers, locator, companion, events.
- Prefer the real `CSharpScanner` on a small `TempProject` over mocking the scanner.
- Keep the whole suite fast. Nothing may shell out to real `dotnet test`, Stryker, or tmux.
  Use the fakes.
- The suite runs on Linux, Windows and macOS. Compare paths after `ProjectPaths.Absolute` or
  `Path.GetFullPath`, which normalise separators on Windows. Mark tests of Windows-only behaviour `[WindowsFact]`.
- After a change, dogfood it: run `crap`, then `mutate` on the files you touched. Surviving
  mutants mean missing tests. As of September 2026, `Main.Program`, `Adapters.Companion.Desktop`,
  `Adapters.Files.ProjectWatcher` and `Adapters.Web.WebServer` have no coverage. They are the
  worst CRAP hotspots.

## Things that must change together

- **CLI.** Change `Main/Program.cs` (the commands and the `Help` text), `Adapters/Cli/CommandLine.cs`
  (`Flags` lists the options that take no value), and the Commands table in `README.md` together.
- **Policy keys (`milligram.json`).** Change `Domain/Policies/Policy.cs`, the README's Configure
  section, and `AgentBriefing.Text` together.
- **Mail protocol.** Change `Domain/Mail/MailMessage.Ops`, `ViewerActions` (viewer → agent),
  `Program.Tell` and `ProjectWatcher.DeliverMail` (agent → viewer), the mail handling in `app.js`,
  and `AgentBriefing.Text` together.
- `AgentBriefing.Text` holds the instructions for the companion agent. Milligram writes them into
  *each examined project* as `.milligram/agent.md`. They are not instructions for you, but they are
  product copy, so keep them accurate.

## Known limits

- The scanner puts every file into one ad-hoc Roslyn compilation in memory, with no MSBuild.
  That is simple and needs no build, but memory and time grow with the codebase. At millions of
  lines, this is the first thing that needs work.
- C# only. `ILanguageScanner` is the seam for other languages.
- Coverage comes from coverlet (Cobertura) and mutation from Stryker.NET. Both are .NET-specific
  readers behind ports.

## Git

- Commit subjects are imperative and short: "Keep metrics snapshots local and write them in sorted
  order". The body says why, and gives measured numbers where relevant (coverage, mutation score).
- Run `dotnet build`, `dotnet test`, and `dotnet format --verify-no-changes` before every commit.
  CI (`.github/workflows/ci.yml`) runs the same checks on every PR, in Release with `-warnaserror`:
  build and test on Linux, Windows and macOS, and the format check on Linux.
- `.gitattributes` keeps every file LF on every OS. Raw string literals take the line endings of their
  file, so a CRLF checkout would change the agent's briefing and break tests.
- Don't commit `.milligram/`, `artifacts/`, `StrykerOutput/`, `bin/`, or `obj/`. The `.gitignore`
  covers them.
