# Legacy .NET Framework investigation

Measured on Windows with .NET SDK 10.0.401 and Milligram commit `0e25ca5`, September 30, 2026.
This records evidence for [#27](https://github.com/jhnoor/milligram/issues/27); it does not declare
legacy build, coverage or mutation support complete.

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

This application has no tests, and this machine has no `msbuild`, `nuget` or `vstest.console`
on PATH. No successful legacy build, coverage collection or mutation run is claimed.
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

1. **Resolve legacy references.** Explicit, unconditional HintPaths now work (see below).
   Resolve conditional/property-based package references and the target framework's reference
   assemblies, with explicit diagnostics for missing inputs. Avoid
   mixing incompatible core libraries in the scanner's single compilation. Acceptance:
   the sample's Controller and DbContext dependencies bind, and modern SDK projects retain
   their existing results. Test missing packages and solutions mixing target frameworks.
2. **Respect legacy Compile membership.** Evaluate explicit, linked and conditional source
   inputs and preprocessor symbols. Keep a documented source-only fallback when evaluation
   is unavailable. Acceptance: the unlisted probe disappears, included/linked files remain,
   inactive configurations do not invent dependencies, and excludes still apply. Include SDK
   generated-input membership: disabling `ImplicitUsings` can leave an old
   `obj/Debug/net10.0/App.GlobalUsings.g.cs` on disk that the scanner still includes. Reproduced
   with an unqualified `HttpClient` property: the dependency remains after disabling the setting.
   File timestamps alone cannot fix this; the SDK writes generated usings only when their
   content changes, so valid generated files can also be older than the project definition.
3. **Prove legacy coverage collection.** Choose a real old-style test project that builds on
   Windows, record the VS Build Tools/runner/collector versions, and produce Cobertura.
   Add any supported runner through the process port. Acceptance: failing tests remain
   visible and a covered/uncovered source line reaches the correct live card.
4. **Prove Framework mutation setup.** Use a supported Framework target and real tests;
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
collect coverage or run mutation. The sample still has no tests. Those acceptance gaps remain
[#38](https://github.com/jhnoor/milligram/issues/38) and
[#39](https://github.com/jhnoor/milligram/issues/39). Shared-file and multi-target metric attribution
is tracked separately in [#64](https://github.com/jhnoor/milligram/issues/64).
