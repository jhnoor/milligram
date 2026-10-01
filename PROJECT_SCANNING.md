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

The smoke test now uses a shorter temporary installation directory while retaining spaces. The
product diagnoses Windows host configuration paths of 260 characters or more and recommends a
shorter `--tool-path`. It does not yet relocate the host or make long legacy-host paths work. The
source/project fixture itself retains long paths and spaces. Its assertions are unchanged.
[#63](https://github.com/jhnoor/milligram/issues/63) tracks support or prompt failure for long
Windows installations; the exact CLR failure detail was not captured.

## Work still required

Large real evaluated project graphs still need measured acceptance. Multi-target selection and
legacy targeting-pack/reference resolution also need the acceptance work in
[#36](https://github.com/jhnoor/milligram/issues/36). Linked files shared by multiple compiler contexts
also need project-aware metric ownership and report attribution; see
[#64](https://github.com/jhnoor/milligram/issues/64). Their per-context coverage and mutation scores
are not yet reliable. Arbitrary tasks may read environment variables,
network resources or files not declared as project inputs; use Refresh after such changes. Changes
to the SDK selected by global.json require restarting the viewer. The source-only
Roslyn-tree benchmark in `SCALING.md` remains a different workload; its numbers do not describe
MSBuild evaluation.
