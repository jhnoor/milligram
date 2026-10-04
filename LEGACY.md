# Legacy .NET Framework investigation

The initial Windows investigation used .NET SDK 10.0.401 and Milligram commit `0e25ca5`,
September 30, 2026. Later sections record evaluated scanning and real old-style coverage and mutation.
This records scoped evidence for [#27](https://github.com/jhnoor/milligram/issues/27).

## Reference application

[MVC Music Store](https://github.com/SebastiaanLubbers/MvcMusicStore/tree/e274968f2827c04cfefbe6493f0a784473f83f80),
commit `e274968f2827c04cfefbe6493f0a784473f83f80`, has one old-style web project targeting
.NET Framework 4.5. Its project explicitly lists 27 `Compile` items and uses `packages.config`,
MVC 4 and Entity Framework 5. It has no test project. The downloaded source was inspected and
scanned; none of its build targets, application code or package install scripts were executed.

Commands, using a separately published Milligram:

```powershell
git clone https://github.com/SebastiaanLubbers/MvcMusicStore.git
git -C MvcMusicStore checkout e274968f2827c04cfefbe6493f0a784473f83f80
milligram init --project MvcMusicStore
milligram ir --project MvcMusicStore
milligram doctor --project MvcMusicStore
```

## Findings

| Check | Observed result |
|-------|-----------------|
| First scan | 27 source files, 32 types, 69 dependencies, about 2.9 seconds. |
| Inferred levels | Models at L0; Filters and ViewModels at L1; Controllers at L2. |
| Suggested libraries | System.ComponentModel, System.ComponentModel.DataAnnotations and System.Transactions. |
| Expected framework/library dependencies | `HomeController : Controller` and `MusicStoreEntities : DbContext` do not produce their System.Web / System.Data.Entity edges. |
| Doctor | Reports old project format, .NET 10 reference mismatch and ignored Compile/build conditions; does not recommend `dotnet restore` for legacy assets. Correctly reports no tests. |

Adding `System.Web` and `System.Data.Entity` to `foreign` did not restore their missing edges.
The checkout initially contains only `packages/repositories.config`, so package availability
was checked separately: the MVC and Entity Framework DLLs were extracted from their exact
NuGet versions into the existing project hint paths. No package scripts ran. Rescanning still
produced **32 types, 69 dependencies and zero edges to either library**.

| NuGet package / extracted assembly | SHA-256 |
|-----------------------------------|---------|
| EntityFramework 5.0.0, `lib/net45/EntityFramework.dll` | `3E834A9DB27A29E702A23AF95BAEBF62A96D0F8B6473D8D42F60143D93EFDCB5` |
| Microsoft.AspNet.Mvc 4.0.20710, `lib/net40/System.Web.Mvc.dll` | `5DC4E38AC4B2DF151A9CD3BEF3E60770511BE9CA9CC050CC123B5FB5B717AE5A` |

NuGet normalizes the MVC version to `4.0.20710`; the project's original directory hint is
`Microsoft.AspNet.Mvc.4.0.20710.0`. The DLL was placed at that exact hinted location. This was
a targeted reference probe, not a complete restore of the application's dependencies.

To check project membership, a temporary `Models/MilligramExcludedProbe.cs` declared
`MvcMusicStore.LegacyProbe.ExcludedFromCompile` without adding a Compile item. Milligram drew
it: **28 files and 33 types**. Removing it restored the original counts. The probe was added
for this experiment; the original checkout's file and Compile counts agree.

The implementation explains both observations: `References` reads only the running framework
and `obj/project.assets.json`; `SourceFiles` walks `.cs` files without evaluating the project.
Missing external edges can also distort inferred levels, so the apparently clean diagram is
not evidence that the application has no architectural violations.

## Coverage and mutation limits

At the initial investigation, this application had no tests and this machine had no `msbuild`,
`nuget` or `vstest.console` on PATH. That investigation established no successful legacy build,
coverage collection or mutation run; the later Polly measurements use a configured Windows runner.
The tool's Cobertura import is covered by the package smoke test using a synthetic report;
that does not prove a collector can instrument this application.

[Stryker's prerequisites](https://stryker-mutator.io/docs/stryker-net/getting-started/) require
NuGet on PATH and NuGet build tasks for Framework projects.
[Its configuration](https://stryker-mutator.io/docs/stryker-net/configuration/) requires a
solution path; MSBuild is auto-discovered, with a CLI-only `--msbuild-path` override.
Milligram runs Stryker from each source project, so put `solution` in that directory's
`stryker-config.json`. Milligram does not forward the MSBuild override today.
[Stryker's stated support goals](https://github.com/stryker-mutator/stryker-net/blob/master/docs/technical-reference/introduction.md)
name Framework 4.6+, while this sample targets 4.5. Compatibility needs an actual configured
build and test run; changing the target framework is not part of this investigation.

## Follow-up issues from #27

These scopes became [#36](https://github.com/jhnoor/milligram/issues/36),
[#37](https://github.com/jhnoor/milligram/issues/37),
[#38](https://github.com/jhnoor/milligram/issues/38) and
[#39](https://github.com/jhnoor/milligram/issues/39). The text below records the original gaps;
the evaluated-scan measurement later in this document supersedes the source-only limitations.

1. **[Resolve legacy references (#36)](https://github.com/jhnoor/milligram/issues/36).** Explicit, unconditional HintPaths now work (see below).
   Resolve conditional/property-based package references and the target framework's reference
   assemblies, with explicit diagnostics for missing inputs. Avoid
   mixing incompatible core libraries in the scanner's single compilation. Acceptance:
   the sample's Controller and DbContext dependencies bind, and modern SDK projects retain
   their existing results. Test missing packages and solutions mixing target frameworks.
2. **[Respect project source membership (#37)](https://github.com/jhnoor/milligram/issues/37).** Evaluate explicit, linked and conditional source
   inputs and preprocessor symbols. Keep a documented source-only fallback when evaluation
   is unavailable. Acceptance: the unlisted probe disappears, included/linked files remain,
   inactive configurations do not invent dependencies, and excludes still apply. Include SDK
   generated-input membership: disabling `ImplicitUsings` can leave an old
   `obj/Debug/net10.0/App.GlobalUsings.g.cs` on disk that the scanner still includes. Reproduced
   with an unqualified `HttpClient` property: the dependency remains after disabling the setting.
   File timestamps alone cannot fix this; the SDK writes generated usings only when their
   content changes, so valid generated files can also be older than the project definition.
   The targeted fix in [#40](https://github.com/jhnoor/milligram/issues/40) filters stale SDK
   namespaces when the project explicitly sets `ImplicitUsings` to `disable` or `false`,
   preserving local `Using` items, aliases and custom namespaces. Imported and conditional
   `Using` items still need project evaluation; the scanner cannot infer their effective values.
3. **[Prove legacy coverage collection (#38)](https://github.com/jhnoor/milligram/issues/38).** Choose a real old-style test project that builds on
   Windows, record the VS Build Tools/runner/collector versions, and produce Cobertura.
   Add any supported runner through the process port. Acceptance: failing tests remain
   visible and a covered/uncovered source line reaches the correct live card.
4. **[Prove Framework mutation setup (#39)](https://github.com/jhnoor/milligram/issues/39).** Use a supported Framework target and real tests;
   validate solution resolution, NuGet/MSBuild discovery and a known surviving mutation.
   Decide whether Milligram needs a documented MSBuild override. Acceptance: a configured
   run succeeds, missing prerequisites produce useful diagnostics, and full/differential
   results and source links agree.

## Follow-up measurement: explicit HintPaths

The scanner now loads managed assemblies from explicit, unconditional Reference/HintPath
entries, including the Windows separators used by old projects. This reads metadata without
executing application code. It reports absent/invalid assemblies and paths that require MSBuild
property or condition evaluation. Runtime framework references and restored NuGet assets retain
precedence; the scanner still uses one compilation and cannot reconcile incompatible targets.

With the same two DLLs, source and `foreign` settings from the investigation, the scan now
finds **32 types and 95 dependencies**. It restores **12 System.Web edges and 11
System.Data.Entity edges**, including HomeController → Controller and MusicStoreEntities →
DbContext inheritance. Remaining packages were not restored, and their missing paths are now
reported. This does not prove that every dependency binds or remove the other limits above.

## Evaluated framework and package inputs

On October 1, 2026, the evaluated scanner from `5ed50fd` rescanned the same pinned application
on Windows x64 with .NET SDK 10.0.401. The original `.csproj` and tracked source were unchanged.
Its project-file SHA-256 remained
`FCBF302346268DC13D707336361E7E67AE364EB276F16A761EF09798279D57F9`.

This machine has no Visual Studio or Web Build Tools. An unprepared evaluated scan failed promptly
on the missing `Microsoft.WebApplication.targets` import and preserved the old model. The prepared
copy used all 29 original `packages.config` packages, extracted into their original versioned hint
paths without running package install scripts, plus two pinned build inputs:

| Package | Version | Package SHA-256 |
|---------|---------|-----------------|
| Microsoft.NETFramework.ReferenceAssemblies.net45 | 1.0.3 | `23A9F94EA3E2CB88CD8341AF75B811C6FB5CB82516FC696E95ED4620279128E3` |
| MSBuild.Microsoft.VisualStudio.Web.targets | 14.0.0.3 | `74B942705CB634BFC5EE8786FAA801CF9634CDD905511F273D3BBC8A60654419` |

An added `Directory.Build.props` set `VSToolsPath` to the web package's `tools/VSToolsPath` directory.
An added `Directory.Build.targets` imported the reference package's `build/Microsoft.NETFramework.ReferenceAssemblies.net45.targets`.
This is explicit fixture setup, not something Milligram installs or changes in examined repositories.
The original target framework remained v4.5. Microsoft documents the
[reference-assembly package alternative](https://learn.microsoft.com/en-us/dotnet/framework/migration-guide/reference-assemblies);
the [web-target package](https://www.nuget.org/packages/MSBuild.Microsoft.VisualStudio.Web.targets/14.0.0.3)
contains the Visual Studio 2015 web targets.

With the same five `foreign` prefixes as the earlier 95-edge measurement, the scan took about
4.0 seconds and produced **27 evaluated source files, 32 types and 98 dependencies**:

- System.Web now has 15 edges, including newly recovered AppConfig, BundleConfig and WebApiConfig dependencies.
- System.Data.Entity retains 11 edges, including MusicStoreEntities → DbContext inheritance.
- HomeController → Controller inheritance remains present. MvcApplication → System.Web changes
  from a generic dependency to correctly bound HttpApplication inheritance.
- The unlisted Compile probe is excluded. No compiler declaration error or MSBuild failure was
  reported. Roslyn still warns that it is using the .NET Core SDK because Visual Studio is absent.

The reproducible check creates a fresh temporary checkout, records all downloaded package hashes
and leaves its model, log and result JSON under `.milligram/evidence` inside that checkout:

```powershell
dotnet publish src/Milligram -c Release -o .milligram/dogfood
./.github/scripts/legacy-scan-fixture.ps1 -ToolPath (Get-Command dotnet).Source `
  -PrefixArguments (Join-Path $PWD '.milligram/dogfood/Milligram.dll')
```

Unlike the original source-only investigation, this scan executes design-time build targets.
It does not execute the application, run package install scripts, build a deployable web application,
collect coverage or run mutation. The sample still has no tests. The separate Polly measurement
below covers [#38](https://github.com/jhnoor/milligram/issues/38), and the mutation measurement covers
[#39](https://github.com/jhnoor/milligram/issues/39). Shared-file and multi-target metric attribution
was completed separately in [#64](https://github.com/jhnoor/milligram/issues/64).

## Follow-up measurement: stale SDK imports

On October 1, 2026, a local .NET 10 project with an unqualified `HttpClient` property was
built and scanned. With implicit usings enabled, the scan contained one HTTP dependency.
After disabling them, the SDK correctly failed compilation with CS0246 but retained its old
generated usings file. The corrected scanner reported zero HTTP dependencies. Adding an
explicit `Using Include="System.Net.Http"` item restored both compilation and the one edge.
This verifies the explicit-setting regression in #40; it does not establish full project or
configuration evaluation under #37.

## Old-style coverage on Windows

On October 4, 2026, [the real coverage fixture](https://github.com/jhnoor/milligram/actions/runs/37235267810)
passed on Windows using Polly 5.0.6 at
[`1a3bf7bf33cfeccce2e224f29cb273e7d333528c`](https://github.com/App-vNext/Polly/tree/1a3bf7bf33cfeccce2e224f29cb273e7d333528c).
Its original old-style `Polly.Net45.csproj` and `Polly.Net45.Specs.csproj` target .NET Framework 4.5
and import linked source from shared projects. Neither project was retargeted or rewritten.

| Input | Measured version |
|-------|------------------|
| Windows runner | Windows build 26100, x64 (`windows-2025`) |
| Visual Studio / full MSBuild | 18.10.12217.157 / 18.10.1.42706 |
| .NET SDK | 10.0.401 |
| Reference assemblies | Microsoft.NETFramework.ReferenceAssemblies.net45 1.0.3 |
| Test framework / runner | Original xUnit 2.1.0 / xunit.runner.console 2.9.3, net48 |
| Test runtime | .NET Framework 4.8, CLR 4.0.30319.42000 |
| Collector | dotnet-coverage 18.11.2, running on .NET 8.0.31 |

The fixture restores the original eight `packages.config` packages by extraction into their hint
paths, without install scripts. An added `Directory.Build.targets` imports the reference-assembly
package; its SHA-256 is the same pinned value documented above. A full checkout with a local
`master` branch at the pinned commit supplies the historical branch name required by
GitVersionTask 3.1.2. Its original build targets remain enabled. Build completed with no warnings
or errors. SDK MSBuild alone failed locally because that old task requires
`Microsoft.Build.Utilities.v4.0`; use full Visual Studio MSBuild for this build.

All **1,204 original tests passed**, with no skips or runner errors, and produced real Cobertura.
A second run selected `ContextSpecs.Should_assign_ExecutionKey_from_constructor`: one test passed,
and the collector exited 0. Temporarily changing that test's expected string produced one failure,
a collector exit of 1, and another real report. The fixture restored the original test bytes and
rebuilt; a final Git diff confirmed that tracked source and project files were unchanged.

Milligram imported both single-test reports into the separately evaluated Release compiler context.
The model contained 80 types; the snapshots contained 582 members and report paths for 94 files.
Both snapshots and the live viewer card showed `Polly.Context.ExecutionKey` at 100% coverage
(line 74) and `ExecutionGuid` at 0% (lines 79–86), with current source hashes. The viewer's source
API returned the exact linked file, `src/Polly.Shared/Context.cs`, with valid member spans.

Reproduce on a Windows machine with full Visual Studio MSBuild, .NET 10 SDK, the collector's
.NET 8 runtime and .NET Framework 4.8 installed:

```powershell
dotnet publish src/Milligram -c Release -o .milligram/legacy-tool
./.github/scripts/legacy-metrics-fixture.ps1 `
  -ToolAssembly (Join-Path $PWD '.milligram/legacy-tool/Milligram.dll') `
  -MsBuildPath (Get-Command msbuild).Source
```

The script downloads the pinned source and packages into a fresh temporary checkout. It retains
that checkout and writes reports, logs, package hashes, runner outcomes, snapshots and the live
card under `artifacts/legacy-metrics`. The dedicated **Legacy metrics** workflow uploads that evidence.

This verifies external collection and `milligram crap --coverage report.xml` for this specific
old-style setup. Automatic `milligram crap` collection still requires SDK-style test projects and
reports that prerequisite before running them. An imported Cobertura file does not convey test
outcomes: retain the original runner log and check its exit code separately, including when it
produces coverage after a failure. No new collector integration is needed for this import path.
This coverage result does not establish other legacy runners, the MVC application's build or
acceptance of a company repository. Framework mutation was measured separately below.

## Old-style mutation on Windows

The [complete mutation fixture](https://github.com/jhnoor/milligram/actions/runs/37241803559)
passed at Milligram `856d55d` using the same pinned Polly source, original Framework 4.5 projects
and Windows build environment above. The original 1,204 tests passed during preparation.
The tests ran on Framework 4.8; the projects were not retargeted.

Additional setup was confined to the scratch checkout:

- A local tool manifest pins **Stryker.NET 4.14.2**, which needs the .NET 8 runtime alongside
  Milligram's .NET 10 SDK. Milligram's own tool manifest remains on Stryker 5.
- NuGet **6.14.0.116** and full Visual Studio MSBuild are on PATH. MSBuild discovery worked
  automatically after #76 stopped the scanner's SDK environment from leaking into child tools.
  No `--msbuild-path` override is required by this measured setup.
- A separate solution selects the original `Polly.Net45` library and `Polly.Net45.Specs` project.
  The source project's `stryker-config.json` sets `solution`, `concurrency: 2`,
  `break-on-initial-test-failure: true` and debug verbosity.
- `Directory.Build.targets` sets `IsTestProject` for the old `packages.config` test project and
  imports the official **xunit.runner.visualstudio 2.4.5** adapter props into that project only.
  The adapter runs under Framework 4.8; its package SHA-256 is
  `1AFED4D553CA7CD6FB20E5ABC141942807AEACAB44DC0B5099E80314D9181C79`.

Stryker 5.0.0 reached an injected-helper compilation failure in this setup: its six-argument
`MemoryMappedFile.CreateFromFile` call does not match the Framework reference API (CS7036).
The same call was found in the 4.15.0 and 4.16.0 sources. The tested 4.14.2 pin predates it.
This is a measured compatibility pin, not a claim about untested Stryker versions.

Full mutation of the original `Context.cs` and `CircuitStateController.cs` produced **42 killed,
5 surviving and 2 uncovered mutants**, with no timeouts. One compile-error mutant was excluded.
The snapshot has 26 entries, including initializer entries. Live cards for both types showed
current hashes, the same scores and surviving sites, and exact linked-source text and valid spans.
An unchanged rerun skipped Stryker and preserved the snapshot byte for byte.

Temporarily rewriting `!_executionGuid.HasValue` as `_executionGuid.HasValue == false` caused
differential mutation to test **three mutants, all killed**, only inside `ExecutionGuid`.
The raw Stryker report verified that scope, and every untouched snapshot entry stayed identical.
Both live cards remained correct. Separate intentional build failure, failing original test and
missing-Stryker runs each exited 1, retained actionable diagnostics and preserved the prior snapshot.
All temporary source edits were restored; a final Git diff confirmed unchanged tracked source and projects.

Reproduce after the coverage preparation above, with .NET 8, .NET 10, full MSBuild and NuGet available:

```powershell
$coverage = Get-Content artifacts/legacy-metrics/result.json -Raw | ConvertFrom-Json
./.github/scripts/legacy-mutation-fixture.ps1 `
  -ToolAssembly (Join-Path $PWD '.milligram/legacy-tool/Milligram.dll') `
  -SampleRoot $coverage.sample
```

Use a freshly prepared checkout for each run. The **Legacy metrics** workflow runs both fixtures
and uploads the mutation result, full/differential snapshots, live cards, logs and raw Stryker reports
as `legacy-mutation`. This proves the pinned original project pair and runtime combination;
other Framework targets, adapters and company repositories still need their own acceptance runs.
