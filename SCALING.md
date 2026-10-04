# Large source-tree check

On 2026-10-01, Milligram initialized a pinned Roslyn checkout with **18,110 C# files and
6,500,426 physical lines** in 386 seconds. The scan found 28,153 types. This exposed and
reproduced two onboarding defects: silent initialization and a crash on repeated type names.
Both are addressed in [#47](https://github.com/jhnoor/milligram/pull/47).

This is a source-scanning check on one Windows machine. It does not establish full project
binding, production-only line counts, coverage or mutation support for Roslyn, or performance
on every platform. These measurements predate the default evaluated project scanner; use
`--source-only` to select the measured scan mode. [Project-input checks](PROJECT_SCANNING.md)
record the separate evaluated-scanner evidence.

## Input and environment

- [dotnet/roslyn at 9546dd2f86dc2bef10c8ac8a95ae20d02cca58ef](https://github.com/dotnet/roslyn/tree/9546dd2f86dc2bef10c8ac8a95ae20d02cca58ef),
  checked out without restoring packages or running its build targets.
- Windows 10 build 19045, x64; .NET SDK 10.0.401.
- AMD Ryzen 5 3600X, 6 cores / 12 threads; 32 GiB RAM.
- Automatic initialization selected `src`. It excluded `bin`, `obj` and six directories
  identified as test projects. The scanned files contained 262,813,083 bytes.
- The whole `src` directory contained 18,176 C# files and 6,506,140 physical lines. These
  include test fixtures, generated source fixtures and source text embedded in strings.
  Imported test-project settings are not evaluated, so many test files remain in the scan.

## Initialization measurements

Each command ran in a fresh process. Wall time includes initialization and policy inference.
Peak working set was sampled every 250 ms. These were single desktop runs, not a controlled
speed comparison; background activity and filesystem caches can affect the measurements.

| Build | Outcome | Wall time | First console output | Peak working set |
|-------|---------|-----------|----------------------|------------------|
| CI preview `0.0.0-ci.36841128054.1` | Measurement stopped at five minutes | 300.9 s | None before cutoff | 7.75 GiB |
| Progress fix, `900ab17` | Scan finished; policy inference crashed on duplicate type ID | 397.3 s | 3.33 s | 8.84 GiB |
| Progress and type-identity fixes in #47 | Policy written, exit 0 | 386.1 s | 1.88 s | 9.18 GiB |

The five-minute cutoff was a measurement guard, not a product timeout or a completed baseline.
The last two scans took 390.3 and 384.2 seconds respectively. Memory use remains substantial;
these changes do not claim to make scanning faster or reduce its memory use.

The failing ID was `Microsoft.AspNetCore.Mvc.Razor.RenderAsyncDelegate`, declared in multiple
Razor shim versions. Seven repeated type names were reported in the successful scan.
File-qualified identities preserve separate Roslyn symbols and their dependency edges; valid
file-local types also receive stable file-qualified IDs. Ordinary and partial types retain
their existing IDs. The result contained 28,153 types and 253,877 dependency pairs during
initialization, which discovers foreign namespaces before selecting library groups.

The subsequent viewer scan completed in 399.4 seconds with 28,153 types and 225,090 dependency
pairs after applying the inferred library groups. In the browser, all three `RenderAsyncDelegate`
boxes appeared separately; opening the Version 1 shim's card and source displayed the correct
file and declaration. A small file-local-type fixture also verified separate member cards and
edges for two identically named helpers.

The original unfiltered root view contained 11,464 nodes and 13,484 edges. It rendered, but was
too crowded for a useful first screen. The bounded overview measured below addresses
[#51](https://github.com/jhnoor/milligram/issues/51). Imported test-project settings still leave
fixtures in the input. All 28,153 type IDs and 308,716 member IDs in the saved model were unique.

## Reproduce

Use a separate checkout with room for the source and at least the measured memory headroom:

```bash
git clone --config core.longpaths=true --filter=blob:none https://github.com/dotnet/roslyn roslyn-scale
git -C roslyn-scale checkout 9546dd2f86dc2bef10c8ac8a95ae20d02cca58ef
milligram init --project /absolute/path/to/roslyn-scale --source-only
milligram serve --project /absolute/path/to/roslyn-scale --source-only --no-agent --no-browser
```

The clone command enables long paths for this checkout on Windows. The measurement used an
isolated Release publish of Milligram and the equivalent
`dotnet /path/to/Milligram.dll` commands. No Roslyn build, restore, coverage or mutation command
was run. Initialization writes `milligram.json` and adds Milligram's local files to `.gitignore`.

## Semantic-model lifetime

A follow-up on the same machine compared two fresh `ir` processes using the same checkout,
inferred policy and existing cached model. The baseline was #47 at `738b412`. The candidate
released each type's syntax/semantic-model references after collecting its dependencies;
remaining types in a shared file retain their own references until they are processed.
No forced garbage collection was added, and no builds or tests ran during these measurements.

| Build | Total wall time | Reported scan time | Peak working set | Peak private memory |
|-------|-----------------|--------------------|------------------|---------------------|
| Baseline | 403.2 s | 397.3 s | 11.44 GiB | 11.67 GiB |
| Release completed semantic models | 384.4 s | 378.5 s | 8.86 GiB | 9.09 GiB |

Peak working set fell by **22.5%** in this pair. These are single desktop measurements; the
elapsed-time difference does not establish a repeatable speed improvement. The model still
contains 28,153 types and 225,090 dependency pairs. Comparing all 8,862,487 lines of each saved
model, omitting only the root `generatedAt` line and normalizing line endings, produced the
same SHA-256: `342655c806a96c2b251a6dbcb2203c97fe1417325d5204dc05b1b4092449d382`.
This comparison includes every type, member, source span, hash, foreign group and dependency.

## Streaming model writes

The semantic-model candidate still reached 8.86 GiB after a scan-stage peak of about 6.8 GiB.
Typed JSON passed through complete UTF-16 strings before the atomic file write. The follow-up
for [#52](https://github.com/jhnoor/milligram/issues/52) streams JSON to the temporary sibling,
appends the existing LF terminator, closes the stream, then uses the existing replacement and
Windows lock retries. Serialization failures preserve the previous file and remove the temporary
file. Policy edits still use `PolicyText` so hand-written comments survive.

The same pinned checkout, policy and cached model were measured in another fresh `ir` process,
again without concurrent builds or tests. The comparison baseline is #53 at `e638e87`.

| Build | Total wall time | Reported scan time | Peak working set | Peak private memory |
|-------|-----------------|--------------------|------------------|---------------------|
| Release completed semantic models | 384.4 s | 378.5 s | 8.86 GiB | 9.09 GiB |
| Also stream typed JSON writes | 383.9 s | 378.9 s | 6.93 GiB | 6.95 GiB |

Peak working set fell a further **21.8%**, or **39.4%** relative to the original 11.44 GiB
baseline. The final write no longer raised the measured peak above the scan-stage peak.
These remain single desktop measurements, with no demonstrated speed improvement. All
8,862,487 model lines matched the previous output using the timestamp exclusion and line-ending
normalization described above, with the same SHA-256. Unit tests additionally compare exact
UTF-8 bytes, including the final newline, and exercise partial serialization failures and
Windows replacement locks.

## First-run scan reuse

Fresh `serve` previously scanned once to infer its policy, then again to prepare the diagram.
The follow-up for [#49](https://github.com/jhnoor/milligram/issues/49) retains the discovery
model, applies the inferred prefix and selected foreign-library groups, and publishes it after
checking source and project inputs. Changed or unverifiable inputs trigger a fresh scan; later
starts with an existing policy and explicit regeneration still scan current source.

Two fresh `serve --no-agent --no-browser` processes used the same pinned checkout, with its
previous policy and model moved aside before each run. No builds or tests ran concurrently.
The baseline was #54 at `f7e9f81` (its production code is identical to `86a1ab2`). Time below is
from process creation to completion of the initial `Scan` job, when the model is available
through the API. It excludes browser layout time.

| Build | Scans | Initial model ready | Peak working set | Peak private memory |
|-------|-------|---------------------|------------------|---------------------|
| Streaming JSON baseline | 2 | 763.2 s | 7.55 GiB | 7.59 GiB |
| Reuse unchanged initialization (`6d49b1d`) | 1 | 388.4 s | 6.77 GiB | 6.77 GiB |

Startup time fell **49.1%** in this pair. These remain single desktop measurements, with
filesystem-cache and background-activity effects. The candidate's startup job explicitly
reported reuse and finished in 3.2 seconds after policy inference and startup diagnostics.
The baseline readiness monitor initially used the wrong discovery path and then encountered
localhost probe timeouts; its successful job was independently verified over IPv4. Both
times use process-start and job-completion timestamps. The baseline stayed idle until the
monitor error was diagnosed; its recorded memory peak did not increase during that interval.

The generated policies were byte-for-byte identical. All 8,862,487 model lines matched using
the same timestamp exclusion and line-ending normalization above, with the same SHA-256.
This includes the inferred library groups, strongest dependency kinds and summed reference
counts. A separate fresh CLI/browser fixture verified type cards, source links, and live
source updates after reuse. Tests cover changed source/project inputs, edits reverted during
initialization, concurrent policy creation, and existing-policy startup.

To reproduce first-run startup, invoke `milligram serve --no-agent --no-browser` in a fresh
checkout without `milligram.json`. Running `init` first measures existing-policy startup instead.
The Windows measurement predates the follow-up correction to atomic policy creation on Unix;
that correction changes file publication, not scan reuse.

## Bounded overview

The follow-up for #51 pages views that would expand beyond 80 interior boxes. The first
page shows up to 20 real namespaces or types, with a dotted summary for other entries.
At most 12 exterior boxes appear, including a summary when needed. Auto mode packs large
views into a grid; dependency markers retain incoming/outgoing pairs and their reference
counts. Collapsed boxes show internal reference counts and level violations. Small views
retain the existing expanded layout. Names can be filtered on each level without editing policy.

On the same pinned model, the root now contains **29 nodes and 89 dependency bundles**.
Its bundles plus collapsed counts preserve all **2,741,410 references**, including **19,669
violating references**, from the model's 225,090 pairs. These inferred-level violations include
the source-fixture and binding limitations already described; they are not a production-only
architecture assessment.

Local headless Chrome at 1440 × 1000 measured the existing model's first fitted overview in
**1,158 ms**, the next page in **378 ms**, and filtering for `Microsoft` in **867 ms**. Opening
that namespace finished **369 ms** after the search. These are single desktop observations
of API requests plus browser layout and drawing, excluding process startup, cached-model
deserialization and source scanning. They are not cross-platform performance guarantees.

The browser check drills through `Microsoft` → `AspNetCore` → `Mvc` → `Razor`, finds all three
`RenderAsyncDelegate` identities, and opens a card and its source. A generated fixture has
500 global types and 120 nested namespaces: 620 types and references. At 1000 × 700, its
first model arrives over SSE after the empty screen and fits automatically; first layout was
356 ms, paging 96 ms, and filtering for the last global type took 88 ms; its card and source also opened.
The fixture also verifies reference totals, red violations, source links and cameras across
pages, filters, namespace navigation, browser history and proposal switches.

Run the generated check with:

```bash
dotnet run --project tests/Milligram.Browser.Integration -c Release -- chrome --diagram
```

For the pinned checkout's already-generated model, append
`--cached-project /absolute/path/to/roslyn-scale`. This fixture reads the cached model rather
than running Roslyn's build or a fresh scan. Append `--serve` for manual inspection; screenshots
are written under ignored `artifacts/browser/` during automated checks. The generated check
also runs across the browser CI matrix.

## Windows watcher shutdown under load

The first Windows CI run for [#59](https://github.com/jhnoor/milligram/pull/59) exceeded the
watcher's graceful-stop check; the unchanged repeat passed. The original log did not distinguish
a two-second exit timeout from an input-close error. [#60](https://github.com/jhnoor/milligram/issues/60)
tracks that unproven cause.

An October 1 probe on the Windows machine above compared `78d74a7` with the event-queue loop that
waits on input closure while idle. The old `Wait-Event -Timeout 1` held shutdown until a file event
or the next one-second timeout. The new loop checks queued events and waits up to 100 ms on the
input task; closing input wakes that wait immediately. It also disposes the watcher and removes
its event subscriptions before the script finishes. The production two-second grace period and
owned-process-tree fallback are unchanged.

Each case ran 16 watcher lifetimes through `ProcessRunner.Follow` and measured `Stop()` wall time.
The load case ran four watchers concurrently, with 100 file creations per watcher and four CPU
worker threads in a probe restricted to two logical processors. PowerShell was 5.1.19041.6456.
Both scripts had identical probe-only EOF/version markers; no other build or benchmark ran during
the measurements.

| Case | Before median / maximum | After median / maximum |
| --- | --- | --- |
| Idle | 652 / 808 ms | 24 / 28 ms |
| Concurrent file changes and CPU load | 1,046 / 1,365 ms | 251 / 524 ms |

All 64 runs exited gracefully. These observations establish removal of an avoidable wait; they
do not reproduce or establish the exact cause of the original CI failure, or guarantee timing on
other machines. The native fixture now repeats idle and busy lifetimes, verifies source/restore
notifications, and reports shutdown stage, elapsed time, bounded stderr and PowerShell/CLR versions
on failure. The Windows side is covered here; WSL interop still needs platform validation.

## Remaining limits

- The source-only mode measured here combines sources in one compilation, so conditional files,
  framework references and duplicate declarations can bind differently from the real projects.
  The default evaluated scanner now keeps separate compiler contexts; its synthetic graph and
  installed-package fixtures are documented in [PROJECT_SCANNING.md](PROJECT_SCANNING.md).
  Performance and usefulness on a large real company solution still need pilot acceptance.
- [Memory use (#48)](https://github.com/jhnoor/milligram/issues/48): completed semantic models
  are now released, with the measured reduction above. Parsing, compilation and the cached model
  still consume substantial memory; this does not make the measured checkout
  suitable for a low-memory machine.
- Dense detailed arrows can still be hard to read. Use the overview, filter names and open
  namespaces for detail; an actual company pilot remains necessary to assess day-to-day use.
