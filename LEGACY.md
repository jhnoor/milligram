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

## Follow-up work to split from #27

These are proposed issue scopes, not completed features or already-created issues.

1. **Resolve legacy references.** Explicit, unconditional HintPaths now work (see below).
   Resolve conditional/property-based package references and the target framework's reference
   assemblies, with explicit diagnostics for missing inputs. Avoid
   mixing incompatible core libraries in the scanner's single compilation. Acceptance:
   the sample's Controller and DbContext dependencies bind, and modern SDK projects retain
   their existing results. Test missing packages and solutions mixing target frameworks.
2. **Respect legacy Compile membership.** Evaluate explicit, linked and conditional source
   inputs and preprocessor symbols. Keep a documented source-only fallback when evaluation
   is unavailable. Acceptance: the unlisted probe disappears, included/linked files remain,
   inactive configurations do not invent dependencies, and excludes still apply.
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
