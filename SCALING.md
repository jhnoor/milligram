# Large source-tree check

On 2026-10-01, Milligram initialized a pinned Roslyn checkout with **18,110 C# files and
6,500,426 physical lines** in 386 seconds. The scan found 28,153 types. This exposed and
reproduced two onboarding defects: silent initialization and a crash on repeated type names.
Both are addressed in [#47](https://github.com/jhnoor/milligram/pull/47).

This is a source-scanning check on one Windows machine. It does not establish full project
binding, production-only line counts, coverage or mutation support for Roslyn, or performance
on every platform.

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

The unfiltered root view contained 11,464 nodes and 13,484 edges. It rendered, but this is too
crowded for a useful first screen; namespace focus is necessary. [#51](https://github.com/jhnoor/milligram/issues/51)
tracks this onboarding limit, especially while imported test-project settings leave fixtures in
the input. All 28,153 type IDs and 308,716 member IDs in the saved model were unique.

## Reproduce

Use a separate checkout with room for the source and at least the measured memory headroom:

```bash
git clone --config core.longpaths=true --filter=blob:none https://github.com/dotnet/roslyn roslyn-scale
git -C roslyn-scale checkout 9546dd2f86dc2bef10c8ac8a95ae20d02cca58ef
milligram init --project /absolute/path/to/roslyn-scale
milligram serve --project /absolute/path/to/roslyn-scale --no-agent --no-browser
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

The final write still raises peak memory: the candidate's sampled working-set peak was about
6.8 GiB near the end of scanning and 8.86 GiB after completion. Typed JSON currently passes
through a complete UTF-16 string before the atomic file write. [#52](https://github.com/jhnoor/milligram/issues/52)
tracks streaming that output without weakening atomic replacement.

## Remaining limits

- [Project input evaluation (#37)](https://github.com/jhnoor/milligram/issues/37): all source is
  placed in one compilation. Conditional files, framework references and duplicate class
  declarations can still bind differently from the real projects. Distinct IDs prevent the
  crash; they do not resolve ambiguous references or symbols Roslyn has already merged.
- [Memory use (#48)](https://github.com/jhnoor/milligram/issues/48): completed semantic models
  are now released, with the measured reduction above. Parsing, compilation, the cached model
  and JSON output still consume substantial memory; this does not make the measured checkout
  suitable for a low-memory machine.
- [First-run rescan (#49)](https://github.com/jhnoor/milligram/issues/49): `serve` scans again
  after inferring a new policy. A one-command first run therefore pays for two scans. Reuse
  needs to preserve the chosen foreign groups and catch source changes during initialization.
