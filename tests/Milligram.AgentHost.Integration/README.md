# Native agent integration fixture

This console project is intentionally outside `Milligram.slnx` and the fast xunit suite.
It runs the production adapter with real PTYs, child processes, same-user pipes and a
deterministic fake agent. It needs no Copilot login. Full Unix runs require tmux; its
ownership tests use a private server and do not use your existing sessions.

```sh
dotnet build tests/Milligram.AgentHost.Integration -c Release -warnaserror
dotnet run --project tests/Milligram.AgentHost.Integration -c Release --no-build
dotnet format tests/Milligram.AgentHost.Integration/Milligram.AgentHost.Integration.csproj --verify-no-changes
```

The full run checks terminal input, Unicode output, resize, Ctrl+C, doorbell delivery,
owned process-tree cleanup, redirected output draining, followed-process shutdown diagnostics, detached startup, directory aliases,
duplicate hosts, replacement ownership, attachment and terminal-mode restoration.
On Unix it also checks tmux session/server-id reuse and similarly named neighboring sessions.
Temporary fixtures use unique directories and own the processes they stop.

For focused work, append one of these after `--` to the `dotnet run` command:

| Option | Checks |
|---|---|
| `--process-fixture` | Redirected process exit, output draining, cancellation and cleanup. |
| `--watcher-fixture` | Windows only: repeated concurrent watchers, source/restore notifications, and idle/busy input closure. |
| `--detached-fixture` | Detached host startup, project aliases and lifecycle controls. |
| `--attach-fixture` | Interactive attachment and terminal-mode restoration. |
| `--tmux-fixture` | Private tmux ownership, exact targeting and doorbell delivery; Unix only. |

CI runs the full fixture on Linux, macOS and Windows, each on x64 and ARM64. The native host
requires glibc on Linux, Windows 10 version 1809 or later, and `pwsh` on Windows. Optional
`--expected-rid RID` checks that a full run uses the intended architecture.

The Windows watcher checks preserve the production two-second graceful-stop deadline. On failure,
they report whether input closure failed or the deadline elapsed, along with bounded stderr and
PowerShell/CLR versions. Busy cases exercise source edits, renames, deletes, restore assets and
generated usings while rejecting ignored build source. These are Windows-side checks; they do not
establish the WSL interop boundary.

These checks do not establish real Copilot login or full-screen rendering, WSL, physical
terminal applications or classic Windows console acceptance. Browser behavior has a
[separate fixture](../Milligram.Browser.Integration/README.md). Independent security review
and the daily-use rollout requirements remain in [AGENT_HOST.md](../../AGENT_HOST.md).
