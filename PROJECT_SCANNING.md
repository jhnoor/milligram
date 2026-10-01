# Project-input scan checks

The experimental `ir --msbuild FILE.csproj [--configuration NAME]` path addresses the compiler-input
portion of [#37](https://github.com/jhnoor/milligram/issues/37). It is explicit so that startup reuse
and live invalidation cannot silently claim freshness for imported targets, external linked files
or generated compiler inputs they do not yet track.

## Automated checks

`ProjectScannerTests` compiles small source graphs in memory. It checks project-local symbols and
imports, duplicate and linked type identities, generic project-reference edges, policy exclusions,
physical source boundaries, deterministic ordering and preservation of the previous model after
evaluation failure. These run in the fast suite without executing MSBuild.

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

Roslyn's design-time loader ignores missing imports, so Milligram first requires ordinary MSBuild
evaluation to succeed, using `-getProperty` without build targets. The workspace then obtains each
project's compiler inputs. Reference-resolution failures can still yield usable source membership;
the command reports the resulting incomplete bindings. This does not establish legacy build,
coverage or mutation support, and it does not replace the pinned evidence in `LEGACY.md`.

## Work still required

Before using this path by default, #37 needs project discovery and policy selection, watcher
invalidation for all imported/project/generated inputs, safe initialization reuse, and measurements
on a large real project graph. Multi-target selection and legacy targeting-pack/reference resolution
also need the acceptance work in [#36](https://github.com/jhnoor/milligram/issues/36). The source-only
Roslyn-tree benchmark in `SCALING.md` remains a different workload; its numbers do not describe
MSBuild evaluation.
