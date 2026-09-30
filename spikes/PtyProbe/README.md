# PTY spike for #20 — do not merge

This standalone experiment evaluates the terminal dependency for Milligram's future Copilot host.
It is outside the product solution and fast test suite. It does not change the companion, add a
viewer terminal, start Copilot, publish a package, or establish that #20 is complete.

## Candidate and source review

**Provisional candidate: Porta.Pty 2.2.2**, pinned together with its transitive ConPTY dependency
in `packages.lock.json`. Its NuGet metadata identifies source commit
[`016b6ea`](https://github.com/tomlm/Porta.Pty/tree/016b6ea14d18aa1f44537aaf78e7c145dae88c76).
It has an MIT license. The [native shim](https://github.com/tomlm/Porta.Pty/blob/016b6ea14d18aa1f44537aaf78e7c145dae88c76/src/Porta.Pty.Native/porta_pty.c)
keeps Unix fork/exec outside managed code, and the package supplies Linux and macOS native assets.
Windows uses ConPTY. This avoids introducing a native build and distribution pipeline of our own.

Ghostflyby.Pty 1.0.0 is Apache-2.0 licensed. Its package points to
[`3c17c9a`](https://github.com/ghostflyby/Pty.Net/tree/3c17c9a80203931898bbba922cd4d479200e811e).
The [published Unix implementation](https://github.com/ghostflyby/Pty.Net/blob/3c17c9a80203931898bbba922cd4d479200e811e/Pty.Net/PtyProcess.Start.Unix.cs)
calls `fork` and then a managed `ChildMain`, using prepared methods and a no-GC region.
That needs a runtime-safety argument beyond this probe, so it was not selected for the first run.
This is a source-review finding, not a claim that its tests fail. Repository-page summaries can
lag the package source; the commit linked by the package is the reference here.

Writing our own ConPTY/native Unix helper remains an alternative if the candidate fails the
required behavior or packaging checks. No production dependency decision has been finalized.

## What the probe measures

The child is this same executable running a small raw-key loop. It verifies:

- attached console handles, plus opening `/dev/tty` on Unix to prove a controlling terminal;
- quoted arguments, a working directory containing spaces, and explicit environment values;
- initial dimensions and resize, with `SIGWINCH` observed on Unix;
- Ctrl+C arriving as a key while the child remains usable;
- a doorbell-like line submitted only after carriage return;
- UTF-8 output written and read one byte at a time, decoded with a persistent decoder;
- exit notification and code 17, then forced shutdown while a read is pending;
- cancellation and resumption of an idle read in asynchronous I/O mode.

Both blocking and asynchronous modes are exercised. Porta's asynchronous mode requires Linux
5.3 or later; its blocking mode is a separate option. A production host must account for that
limit instead of silently requiring a newer kernel. This harness is not an xterm.js renderer test.

## Reproduce

From the repository root with .NET 10:

```powershell
dotnet restore spikes/PtyProbe --locked-mode
dotnet build spikes/PtyProbe -c Release --no-restore -warnaserror
dotnet run --project spikes/PtyProbe -c Release --no-build -- --output artifacts/pty-results/direct-async.md
dotnet run --project spikes/PtyProbe -c Release --no-build -- --blocking --output artifacts/pty-results/direct-blocking.md
dotnet pack spikes/PtyProbe -c Release -p:Version=0.0.0-spike.local
./spikes/PtyProbe/smoke.ps1 -Version 0.0.0-spike.local
```

The package is installed to a fresh temporary tool directory and also run through `dotnet dnx`.
It is then installed with `dotnet tool install --global` under a temporary `DOTNET_CLI_HOME`;
the user's existing global tools and PATH are left alone. Each launch path exercises both modes.
The smoke script checks that each requested I/O mode actually reached the probe. Use a new version
after changing the code, since `dnx` caches packages. No package is uploaded to a feed.

The branch-only workflow targets Linux, macOS and Windows on x64 and arm64. It checks the actual
runtime identifier, preserves Markdown result artifacts even on failure, and has a bounded job
timeout.

## Measured results

[Run 36648457626](https://github.com/jhnoor/milligram/actions/runs/36648457626), commit
`3ef7eda12d86580eafe9f98e33ade55efe75aa75`, passed all six native runtime targets on .NET 10.0.12.
Each row represents four passing probes: tool-path install and `dnx`, each in blocking and
asynchronous mode. The latter adds the cancellation check (eight checks rather than seven).
All 24 Markdown artifacts were inspected; no failed checks or unexpected runtime identifiers.

| Runtime | Observed OS | Tool-path install | dnx |
|---|---|---|---|
| linux-x64 | Ubuntu 24.04.5 LTS | Both modes pass | Both modes pass |
| linux-arm64 | Ubuntu 24.04.5 LTS | Both modes pass | Both modes pass |
| osx-x64 | macOS 15.7.9 | Both modes pass | Both modes pass |
| osx-arm64 | macOS 15.7.9 | Both modes pass | Both modes pass |
| win-x64 | Windows build 26100 | Both modes pass | Both modes pass |
| win-arm64 | Windows build 26200 | Both modes pass | Both modes pass |

Local Windows x64, build 19045, also passes all three launch paths, including the new isolated
global-install checks. Hosted global-install results at ordinary path lengths are pending. These OS versions do not prove
the candidate's advertised minimum Windows version or compatibility with every older Linux kernel.

**Windows long-path limit:** [run 36648767857](https://github.com/jhnoor/milligram/actions/runs/36648767857)
passed the added global-install checks on both Linux and both macOS targets, but failed on both
Windows targets with `DllNotFoundException`, `conpty.dll`, error `0x800700CE` (path too long).
Those temporary global installations put the native DLL beyond 260 characters. The same code
and package passed in shorter tool-path and `dnx` locations. The normal smoke now uses a shorter
temporary CLI home; `-GlobalDirectory` preserves the ability to reproduce the long installation.
This is a candidate limitation, not a fixed library bug. A production host needs an explicit
solution or diagnostic for it. Local Windows build 19045 did load the out-of-band library from
a 285-character DLL path, so the failure is environment-dependent, not a universal cutoff.
Reports now record the selected implementation and application path length to make this visible.

The first hosted run exposed two probe assumptions, corrected before the passing run: a working
directory can have different equivalent path spellings on macOS, and terminal dimensions can be
cached until `SIGWINCH` is processed on Unix. The probe now reads a unique relative fixture marker
and waits for the signal before polling the child's actual dimensions.

## Still required before #20 can be closed

Real Copilot login and xterm.js rendering at several sizes, a real Milligram doorbell, WSL2 testing,
and global-install results on the hosted targets. Linux-musl, older supported OS versions and
other architectures remain untested. The fake child cannot establish real-agent results.
Process-tree cleanup, replay, multiple clients, reconnect, WebSocket security and rollout belong
to the subsequent host/panel issues. In particular, this experiment does not justify changing the
default companion or skipping the weeks of daily use required by #26.
