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
The smoke script checks that each requested I/O mode actually reached the probe. Use a new version
after changing the code, since `dnx` caches packages. No package is uploaded to a feed.

The branch-only workflow targets Linux, macOS and Windows on x64 and arm64. It checks the actual
runtime identifier, preserves Markdown result artifacts even on failure, and has a bounded job
timeout. Local Windows x64 checks pass in both modes, including installed-tool and `dnx` runs.
Hosted-runner results are still pending.

## Still required before #20 can be closed

Real Copilot login and xterm.js rendering at several sizes, a real Milligram doorbell, WSL2 testing,
and a recorded result for every target platform. The fake child cannot establish those results.
Process-tree cleanup, replay, multiple clients, reconnect, WebSocket security and rollout belong
to the subsequent host/panel issues. In particular, this experiment does not justify changing the
default companion or skipping the weeks of daily use required by #26.
