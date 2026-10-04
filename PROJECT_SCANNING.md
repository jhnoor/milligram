# Project-input scan checks

Normal startup, `init`, `ir` and viewer refreshes share the policy's `scan` settings. Auto mode
discovers projects under/above `src`; explicit `projects` replace that selection, and `configuration`
selects the build configuration. Source-only mode remains available without project evaluation.
Malformed selected projects fail with a fallback diagnostic instead of silently switching scanners.

## Automated checks

`ProjectScannerTests` compiles small source graphs in memory. It checks project-local symbols and
imports, duplicate and linked type identities, generic project-reference edges, policy exclusions,
physical source boundaries, deterministic ordering and preservation of the previous model after
evaluation failure. These run in the fast suite without executing MSBuild.
Service tests verify that coverage and Stryker receive the selected configuration. Scanner tests
verify that changing configuration, symbols or optimization invalidates old metrics even when the
member's source is unchanged; symbol ordering alone does not change the fingerprint.

`MetricContextTests` covers assembly-separated Cobertura hits, ambiguous imports, partial test
runs, linked-file mutation ownership, scoped snapshot merging, transitive compiler references and
Stryker framework selection. Processes are faked in the fast suite.
`.github/scripts/metric-context-fixture.ps1` runs real coverage against two libraries linking one
physical file with different symbols. One library targets netstandard2.1 and net10.0; only net10.0
is tested, and the other context must stay unknown. With `-Mutation`, it also runs pinned Stryker
5.0.0 on the linked file in both owners and verifies separate killed-mutant entries. The installed
package smoke runs this fixture on Linux, Windows and macOS. Imported reports are separately
checked for ambiguous framework identity.

The package smoke invokes `.github/scripts/project-scan-fixture.ps1` against the **installed tool**.
It creates SDK and old-style projects in a new temporary directory and runs real MSBuild/Roslyn:

- Explicit and conditional `Compile` items omit real, unlisted source files.
- Linked source outside `src` but inside the root remains visible and uses its physical path.
- Debug/Release and SDK framework symbols select the correct declarations.
- An imported `.props` edit changes the next scan without a source edit.
- Implicit imports and custom symbols do not leak into a referenced project.
- A conditional alias `Using` resolves in Release; the project-reference edge survives every scan.
- Disabling implicit usings excludes an existing, stale `obj/Debug/net10.0/*.GlobalUsings.g.cs`.
- An old-style .NET Framework project retains explicit/conditional membership even where its
  framework reference pack is unavailable. Unavailable bindings are diagnosed, never filled with
  the running .NET 10 framework.
- Mixed SDK/old-style entry projects retain their own preprocessor contexts.
- A missing imported target fails evaluation and preserves the previous model byte for byte.

The script prints its temporary evidence directory. It can also exercise a local build:

```powershell
dotnet build src/Milligram -c Release -warnaserror
./.github/scripts/project-scan-fixture.ps1 -ToolPath (Get-Command dotnet).Source `
  -PrefixArguments (Join-Path $PWD 'src/Milligram/bin/Release/net10.0/Milligram.dll')
```

`project-live-fixture.ps1` also starts the installed tool with no policy and no scan flags. It
checks automatic discovery, persisted settings, omission of unlisted source, imported settings
with a nonstandard extension outside the root, linked source edits and new wildcard files outside
`src`, external AdditionalFiles, live Debug/Release changes and custom intermediate output paths.
The configuration change also invalidates the metric fingerprint of an unchanged method.
It checks optional imports appearing/disappearing, idle stability, failed-import model preservation,
and recovery after selecting another project whose external import is initially missing. The
server process is stopped in `finally`.

`framework-scan-fixture.ps1` runs against the installed package in the same three-platform smoke.
It restores the real .NET Framework 4.8 reference-assembly package, builds two small metadata-only
dependencies, and checks conditional/property-expanded HintPaths in an old-style project. It also
checks a pair of SDK projects targeting both net48 and net10.0: each framework retains its own
types, matching project-reference edges and framework APIs (`HttpApplication` versus `Lock`).
Removing a referenced DLL must report incomplete bindings without inventing its edge; restoring
it recovers the dependency. The default scanner does not substitute .NET 10's core library.

The separately runnable `legacy-scan-fixture.ps1` fetches the pinned Music Store application,
extracts its original packages without install scripts, and supplies pinned web targets and net45
reference assemblies through added build-configuration files. It checks the original project file's
hash, exact source/type/edge counts and restored inheritance. See `LEGACY.md` for setup and limits.

Evaluated input snapshots record files returned by MSBuild, its actual import graph, wildcard
matches and Roslyn's documents, additional documents, analyzer configuration and metadata references.
Import declarations are also observed so optional files can appear later. MSBuild's own glob matcher
filters wildcard inputs; watching every file in an import directory would loop on build-cache writes.
The poller compares file existence, size and write time and backs off on expensive walks. It keeps
the pre-evaluation observation of known inputs, so edits during evaluation cause another refresh.
Unrelated output is ignored; explicitly evaluated output files remain tracked. Initialization reuse
also requires the existing content fingerprint and an unchanged evaluated input snapshot. A failed
evaluation retains the previous model, reports the error, and watches repairs to both the attempted
selection and the previous graph. A redundant queued refresh cannot hide that error. Automatic
project discovery is checked again during polling, including new or moved project directories.

Roslyn's design-time loader ignores missing imports, so Milligram first requires ordinary MSBuild
evaluation to succeed, using `-getProperty` without build targets. The workspace then obtains each
project's compiler inputs. Reference-resolution failures can still yield usable source membership;
the command reports the resulting incomplete bindings. This does not establish legacy build,
coverage or mutation support, and it does not replace the pinned evidence in `LEGACY.md`.

## Synthetic project graph

On Windows x64 with .NET SDK 10.0.401, the evaluated path scanned a restored graph of 32 SDK
projects, 1,015,871 physical C# lines and 16,384 types in 40.3 seconds. All 31 expected
project-reference edges were retained and unlisted files were absent. The main process peaked at
1,787,904,000 bytes of working set (about 1.67 GiB); that figure excludes its MSBuild child hosts.
Milligram's own project scanned without binding diagnostics in 8.8 seconds (178 types, 723 edges).

The synthetic graph uses explicit Compile items and a chain of project references, without package
dependencies, analyzers or source generators. These numbers do not establish performance on a
large company's real solution. The source-only Roslyn benchmark remains a different workload.
The graph and assertions are reproducible outside the regular CI suite:

```powershell
dotnet publish src/Milligram -c Release -o .milligram/dogfood
./.github/scripts/project-scale-fixture.ps1 -ToolAssembly (Join-Path $PWD '.milligram/dogfood/Milligram.dll')
```

The script uses a fresh temporary directory, restores its own projects, checks the resulting
model, records elapsed time and main-process peak memory, and leaves its logs and model there.

## Windows installation path limit

The first Windows package CI run at `9130757` passed all SDK cases, then the legacy host failed to
connect within Roslyn's 60-second deadline. Its `.exe.config` path was 264 characters. The same
package reproduced the failure locally with a 264-character configuration path; a 259-character
path evaluated the same old-style project successfully in 2.2 seconds. The host emitted no process
output in the failing case. This is a deep tool-installation-path limitation, independent of whether
Visual Studio is installed locally.

Milligram now rejects a selected legacy project, including evaluated project references, before
Roslyn launches that host when its configuration path is 260 characters or more. The error names
the project and asks for a shorter `--tool-path`; the previous model remains intact. Modern SDK
projects still scan from the same deep installation. The preflight follows the host-selection
rules of the pinned [Roslyn 5.9 source](https://github.com/dotnet/roslyn/blob/35d9211b841e7613c1d2f8f5af6d628ace696c4c/src/Workspaces/MSBuild/Core/MSBuild/BuildHostProcessManager.cs),
including SDK imports and explicit TargetFramework/TargetFrameworks elements, rather than
assuming that every project targeting .NET Framework needs the legacy host.

The Windows installed-package smoke exercises short and exactly 264-character configuration
paths with spaces and Unicode. It verifies modern scans in both locations, legacy evaluation
from the short path, prompt failures for direct/referenced legacy projects at the long path,
and preservation of the previous model. This implements the prompt-failure remedy in
[#63](https://github.com/jhnoor/milligram/issues/63); it does not relocate the host or make legacy
evaluation work from an unsupported installation path.

## Work still required

Large real evaluated project graphs still need measured acceptance. The framework fixtures and
pinned legacy sample provide the source/reference evidence for
[#36](https://github.com/jhnoor/milligram/issues/36); they do not prove legacy build or metric collection.
Imported coverage without unique assembly/framework ownership stays unknown; automatic collection
uses evaluated test references. Arbitrary tasks may read environment variables,
network resources or files not declared as project inputs; use Refresh after such changes. Changes
to the SDK selected by global.json require restarting the viewer. The source-only
Roslyn-tree benchmark in `SCALING.md` remains a different workload; its numbers do not describe
MSBuild evaluation.
