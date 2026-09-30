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

[Run 36649285602](https://github.com/jhnoor/milligram/actions/runs/36649285602), commit
`9abd925`, passed all six native runtime targets on .NET 10.0.12.
Each row represents six passing probes: tool-path install, global install and `dnx`, each in blocking and
asynchronous mode. The latter adds the cancellation check (eight checks rather than seven).
All 36 Markdown artifacts were inspected: 270 passing checks, no failures or unexpected runtime identifiers.
Windows reports select the out-of-band implementation; Linux and macOS report `posix`.

| Runtime | Observed OS | Tool-path install | Global install | dnx |
|---|---|---|---|---|
| linux-x64 | Ubuntu 24.04.5 LTS | Both modes pass | Both modes pass | Both modes pass |
| linux-arm64 | Ubuntu 24.04.5 LTS | Both modes pass | Both modes pass | Both modes pass |
| osx-x64 | macOS 15.7.9 | Both modes pass | Both modes pass | Both modes pass |
| osx-arm64 | macOS 15.7.9 | Both modes pass | Both modes pass | Both modes pass |
| win-x64 | Windows build 26100 | Both modes pass | Both modes pass | Both modes pass |
| win-arm64 | Windows build 26200 | Both modes pass | Both modes pass | Both modes pass |

Local Windows x64, build 19045, also passes all three launch paths, including the new isolated
global-install checks. These OS versions do not prove
the candidate's advertised minimum Windows version or compatibility with every older Linux kernel.

**Windows path-boundary failure and measured workaround.**
[Run 36651496373](https://github.com/jhnoor/milligram/actions/runs/36651496373), commit `ec79ce8`,
reproduces `DllNotFoundException` for `conpty.dll`, error `0x800700CE`, at the original install
boundary. The DLL paths are 257 characters on x64 and 259 on arm64; the packaged `OpenConsole.exe`
is deeper. Merely using a still-longer path does not reproduce it. The original failure is also
preserved in [run 36648767857](https://github.com/jhnoor/milligram/actions/runs/36648767857).

| Windows target | DLL path length | Default startup | Early backend initialization | Extended-path preload |
|---|---|---|---|---|
| Build 26100, x64 | 257 | Both modes fail | Both modes fail | Both modes pass |
| Build 26200, arm64 | 259 | Both modes fail | Both modes fail | Both modes pass |
| Both hosted targets | 330 | Both modes pass | Both modes pass | Both modes pass |
| Local build 19045, x64 | 257 | Both modes fail | Both modes fail | Both modes pass |
| Local build 19045, x64 | 330 | Both modes pass | Both modes pass | Both modes pass |

All three machines reported `LongPathsEnabled: 1`; the experiment does not change that setting.
The opt-in `--preload-long-path` loads the packaged DLL through its absolute extended Windows
path before Porta opens a terminal. It requires the out-of-band backend and a console-host path
over 260 characters. That workaround passes the reproduced failure on both architectures, without
patching the library. It needs to be carried into a production adapter or replaced by an upstream
fix; simply shortening one test directory would leave a real installation failure unresolved.
This measures a workaround, not the native loader's internal cause or every possible path length.

`long-path.ps1 -Version VERSION -ExpectedRuntime win-x64 -Layout boundary` reproduces the failure
and checks the workaround. Use `win-arm64` for an ARM runner and `-Layout long` for the longer
control. Each layout runs both I/O modes through all three arms using the same installed package.
The boundary control must fail with the specific loader error, so a comparison cannot pass without
reproducing it. Backend diagnostics run after startup in the control. The normal six-platform
checks also pass in the same run. No terminal dependency or workaround is added to the product by
this spike.

The first hosted run exposed two probe assumptions, corrected before the passing run: a working
directory can have different equivalent path spellings on macOS, and terminal dimensions can be
cached until `SIGWINCH` is processed on Unix. The probe now reads a unique relative fixture marker
and waits for the signal before polling the child's actual dimensions.

## Still required before #20 can be closed

Real Copilot login and xterm.js rendering at several sizes, a real Milligram doorbell, WSL2 testing,
and carrying the measured Windows loader workaround into the production adapter. Linux-musl, older supported OS versions and
other architectures remain untested. The fake child cannot establish real-agent results.
Process-tree cleanup, replay, multiple clients, reconnect, WebSocket security and rollout belong
to the subsequent host/panel issues. In particular, this experiment does not justify changing the
default companion or skipping the weeks of daily use required by #26.
