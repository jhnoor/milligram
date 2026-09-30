# Agent host protocol (in development)

This branch builds the opt-in host tracked in #22. The default companion remains tmux.
Framing, replay, the terminal session, same-user pipes, project lease, detached host and companion
control are implemented. `agent.host: "milligram"` selects the native host for session commands
and interactive attachment in the terminal or browser. This document describes a development wire contract.
If the policy cannot be read, CLI `agent stop` uses both project backends without selecting one
from the broken file. Native shutdown still uses the pipe and lease, never a discovery PID.

## Framing

A frame is a four-byte little-endian signed length, a one-byte kind, then its payload.
The length includes the kind and must be between 1 and 16,385. End of stream between frames is
a clean disconnect; an incomplete header or body is an error. Unknown kinds and invalid control
payloads are rejected. Each connection has one writer to prevent interleaved frames.

| Kind | Byte | Payload |
|---|---|---|
| hello | 1 | JSON: `protocol` (positive integer), `version` (Milligram version, 1–128 characters), optional `instance` (32 hexadecimal GUID digits); at most 512 bytes |
| output | 2 | Raw terminal bytes, at most 16 KiB |
| input | 3 | Raw terminal bytes, at most 16 KiB |
| resize | 4 | Two little-endian 32-bit integers: columns, rows; each 2–1000 |
| ring | 5 | Empty |
| stop | 6 | Empty |
| status | 7 | Empty request; JSON response: `pid` (agent process), `clients`, `columns`, `rows` |
| exited | 8 | Little-endian signed 32-bit exit code |

Protocol version 1 uses the shared `MilligramJson.Options` for JSON. Both peers will exchange
hello before commands or terminal data; an incompatible version will require restarting the host.
The parser keeps a newer greeting readable so clients can explain incompatibility.

## Replay

The host retains at most 1 MiB of raw output. The buffer tracks UTF-8 characters and ANSI/VT
escape sequences across reads. When older bytes are overwritten, replay starts at the next complete
boundary, including CSI, OSC, DCS, SOS, PM and APC strings and their terminators. It preserves bytes;
it does not reconstruct the terminal screen or its previous colours and modes.

Replay may end mid-character or mid-sequence. A client must keep its decoder/parser state across
replay and live chunks. If an unfinished sequence is longer than the buffer, a new client has no
usable replay and must wait for the next safe live boundary. `Append` reports that offset and
`IsAtBoundary` distinguishes an empty buffer from an unfinished sequence. The host must serialize
subscription, snapshot and output delivery so no bytes are duplicated or lost between them.

## Terminal sessions

`AgentSession` sits behind `IAgentTerminal`. Each client gets a bounded queue of 128 frames,
enough for the complete default replay plus control messages. Queue overflow disconnects that
client; neither a slow reader nor its cancellation callbacks block the terminal output pump.
Subscription, replay and output delivery share a lock. Output chunks own their byte arrays and
fit the wire limit. After the host drains terminal output, `Complete` queues the exit code behind
the remaining output and completes the queues, allowing healthy clients to drain before closing.

Input, resize and doorbell actions share a separate gate. A ring writes the briefing's doorbell,
flushes, waits 150 ms, and writes a carriage return; another client's input cannot split those
steps. Once accepted, a ring finishes even if its requesting client detaches. Ending the session
cancels pending terminal work. Detaching a client does not request an agent stop. A stop frame
signals the owning host, which is responsible for ending and disposing the terminal process tree.

The most recently active client's remembered size applies when it types or resizes. Its first
resize briefly changes the width and restores the requested dimensions to request a redraw.
Real Copilot rendering still needs validation; raw replay is not an exact screen snapshot.

## Local transport

`AgentPipeServer` and `AgentPipeClient` use asynchronous named pipes with
`PipeOptions.CurrentUserOnly` on both ends. Each peer sends its hello first. The server gives
the client three seconds to finish greeting, then disconnects it if it has not. Protocol versions
must match; a client that finds an incompatible host reports the host version and asks for a
restart. Application versions may differ when the protocol is compatible.
The next listening instance exists before a connected client can close, preserving the shared
Unix listening socket across short probes. A peer rejected during acceptance does not stop the
host. Liveness probes retry transient disconnects within their original deadline.

Only input, resize, ring, stop and empty status requests are accepted after greeting. Invalid
frames, incomplete frames, unexpected server-only messages and transport failures disconnect
that client. Each connection has one output task and serialized client writes. A slow connection
inherits the session's bounded queue and cancellation, so it cannot block accepting new clients.
Host shutdown stops new connections and lets existing clients receive output followed by exit
before closing them; cancellation can interrupt idle readers and the listener.

## Project ownership

`AgentHostLease` holds `.milligram/run/agent-host.lock` open with exclusive sharing for the
host's lifetime. It keeps the lock file after closing: deleting it would let competing Unix
processes lock different file objects at the same path. A stale discovery file does not hold
the lock. The process host publishes discovery only after its terminal and listener are ready.
It also takes ownership before opening its log or preparing the agent's conversation and shim;
a duplicate start cannot change these files.

Discovery at `.milligram/run/agent-host.json` is atomic JSON containing the host `pid`, pipe
`endpoint`, `protocol`, Milligram `version`, `started` timestamp and random per-run `instance`. The lease removes only the
exact record it published, before releasing its lock. Repeated disposal cannot affect a new
owner. Missing, unreadable or malformed discovery is read as absent (including a Windows file
pending deletion); liveness must still be checked by
connecting, never inferred from a PID in this file.

Pipe names are `milligram-` followed by 20 ASCII hexadecimal characters derived from the full
physical project root. Directory symlinks, Windows junctions and aliases in ancestor directories
resolve before hashing; Windows uses the directory's stored casing. Tmux uses the same physical
root for its readable name and hash. The selected logical root still controls project paths.
An absent or inaccessible root is an error rather than a second agent identity. Each controller
retains its resolved identity for its lifetime. The host log path is
`.milligram/run/agent-host.log`.

Agent names changed from earlier experimental builds to unify directory aliases. Stop existing
agents with the old build before upgrading, then start them with the new build.

## Native terminal adapter

The opt-in implementation pins Porta.Pty 2.2.2. `ProcessRunner.StartTerminalAsync` uses the
same executable lookup and batch quoting as other child processes, then supplies the terminal
size, project working directory and the agent's shim on PATH. Unix launches receive
`TERM=xterm-256color` and a UTF-8 locale. Async terminal I/O is selected except on Linux kernels
older than 5.3, where the library's blocking mode is required.

On Windows, the adapter preloads the packaged ConPTY DLL through an extended absolute path.
This is the workaround measured at the path-length boundary in the spike; the library handle
stays alive for the process lifetime. Explicit selection of the in-box backend is preserved.
The adapter captures an owned Windows process handle for tree termination; disposing the
connection also closes the library's kill-on-close job. Unix shutdown kills the process group
created by `forkpty`, including a descendant whose agent parent has exited. A process that
deliberately creates a different Unix session is outside that process group.

[The native integration fixture](tests/Milligram.AgentHost.Integration/README.md) is a standalone fake-agent program, outside the fast
test solution. It exercises the production adapter with a real terminal: controlling TTY,
arguments, a Unicode project path, working directory, the actual `milligram` shim, split UTF-8,
resize, Ctrl+C, doorbell submission and nonzero exit. Separate cases exercise forced descendant
cleanup and cleanup after the parent exits, both directly and through the host runtime and its
pipe. The native workflow runs on Linux, macOS and Windows, each with x64 and ARM64 runners.
It does not start real Copilot.

## Host shutdown

`AgentHostRuntime` owns the terminal for one run. It starts the output pump and listener before
invoking the discovery callback. Agent exit, a stop command, cancellation, or a failed output
pump/listener starts cleanup. A clean terminal EOF gets a short grace period for the native exit
event, so ordinary exit codes survive event ordering differences.

Cleanup stops the owned tree, waits for the exit code and remaining output, then announces exit
and stops accepting clients. Each wait has a three-second deadline. Clients can drain before
cancellation closes stalled connections; terminal disposal releases the native handles. Failed
stop, drain or disposal steps are recorded without skipping later cleanup. Only after cleanup
does the runtime write its exit and error messages to the host log.

## Detached process

The internal `agent host --project DIR` command owns the lease, log and native runtime.
`ProcessRunner.StartDetached` separates its standard streams and starts Windows hosts without a
visible console. The caller disposes the returned process handle; that does not stop the host.
On Unix, the host calls `setsid` before creating the agent terminal. Windows launches temporarily
clear inheritance on the launcher's original standard handles, then restore it: redirecting all
three streams alone still allowed the host to retain a captured launcher's output pipe in the
real fixture. This kept the caller waiting even though its immediate child had already exited.

The integration fixture now runs the actual hidden command through a short-lived launcher.
It checks detachment, Unix session ownership, duplicate starts, replacement clients, doorbell,
stop, stale discovery and restart. It also exercises `AgentHostCompanion` directly against the
real command: start, reuse, status, notification and stop.

## Companion control

`AgentHostCompanion` probes the pipe for liveness and waits for published discovery before
returning from startup. If a previous host is still releasing its lease, it waits before
launching. An early startup failure includes a bounded log tail. Cancellation or a startup
deadline can stop only the child handle returned by this launch, never a PID from discovery.
The launcher generates a random instance ID before creating its child and passes it through
the internal `--instance` argument. Startup returns an immutable cleanup callback only when its
own live child, discovery and connected peer identify that instance. A concurrent caller reuses
the winning host and cleans up its losing child. Compatible older hosts without an instance
remain usable but cannot grant fresh ownership. Automatic cleanup checks the instance again on
the connection that will carry stop; a stale discovery or reused PID cannot authorize stopping
a replacement. Explicit stop still targets the current session.

Tmux atomically stores a random environment marker when creating a session and returns its
unique numeric session ID. Cleanup targets that ID and checks its marker in one `if-shell -F`
command. This protects both name reuse and numeric ID reuse after a server restart. A private
tmux integration fixture exercises both cases on Linux and macOS. The viewer retains its initial
cleanup callback across manual restarts, so exiting an old viewer leaves a replacement running.

A notification sends ring followed by status and waits for the status response, ensuring the
doorbell has been submitted. Stop drains the pipe through EOF and waits for the old owner's
lease to be released, or for a different host to publish discovery. A missing discovery record
alone does not release ownership: removal happens just before the lock closes. Stop also handles
a host that is still initializing. Malformed greetings are not treated as running hosts, and
incompatible protocol versions require restart.
Selection is fixed for each viewer run after policy loading. Changing `agent.host` requires a
viewer restart; stop an old session before switching hosts.
The six-platform terminal evidence and Windows loader workaround are on the separate
[`codex/pty-spike` branch](https://github.com/jhnoor/milligram/tree/codex/pty-spike/spikes/PtyProbe).
Real Copilot login and full-screen rendering, WSL and the rollout criteria remain separate acceptance work.

## Local terminal attachment

For the native host, `agent.terminal: "auto"` opens the browser panel and `"none"` leaves
attachment manual. A custom template ends in one whole `{command}` argument, expanded to this
build's executable/assembly arguments followed by `agent attach --project ROOT`. Template
validation and executable lookup participate in `doctor` and startup availability. The command
is captured before host startup, so a policy edit during startup cannot lose acquired ownership.
Only a caller that starts its own host opens the external window; launch failure retains that
ownership and the normal attachment command remains available. An explicit attachment always
uses its current terminal.

Expansion preserves argument boundaries without inserting a shell. Templates must accept an
executable plus arguments, not an interpolated shell script. For `wt`/`wt.exe`, generated
semicolons are escaped with a backslash because the
[Windows Terminal parser](https://github.com/microsoft/terminal/blob/main/src/cascadia/TerminalApp/Commandline.cpp)
treats them as command separators even within an argument. Other terminal templates receive
the unchanged arguments. The native fixture records a real external-launch invocation to verify
the selected build, project and argument boundaries without opening a GUI on the CI runner.

`agent attach` connects to the selected native host, enters raw console mode and relays input,
output and resize frames. It requires terminal input and output. Ctrl+] then d detaches; a doubled
Ctrl+] sends one literal prefix. Detach never sends stop, and another attached client stays connected.
The agent's exit frame becomes the command's exit code. A broken pipe produces an actionable error.

Unix uses libc `tcgetattr`, `cfmakeraw`, `tcsetattr` and bounded `poll` calls; SIGWINCH schedules a
resize. Windows uses VT input and Unicode console records, including window-size events, with
bounded waits. Neither leaves a blocked standard-input reader after detach. A nested ConPTY's
request to enable Win32 input mode is rewritten to disable it, keeping the attachment's detach
key available; other output passes through, with decoding preserved across split UTF-8 chunks.

All relay tasks finish before restoring the saved native console modes. Cleanup also resets the
alternate screen, bracketed paste, cursor visibility and text attributes. Handled hangup/termination
signals detach on Unix; uncatchable termination cannot restore a terminal. The native fixture runs
the public command inside a real terminal and checks Unicode in both directions, Ctrl+C, resize,
detach/reattach alongside another pipe client, agent exit, exact mode restoration and a subsequent
shell read. Unix runners also exercise SIGTERM. Classic Windows console, WSL and real Copilot
remain explicit acceptance checks.
The mode comparison excludes Darwin's kernel-owned `PENDIN` state bit: its tty implementation
sets this when returning to canonical input, independently of the restored configuration.

## Browser transport

The native host exposes a browser attachment through `GET /api/agent/terminal`. `/api/meta`
includes `agent.terminal.available`, the `autoOpen` policy preference and, when available,
`agent.terminal.protocol`. Availability describes the configured transport, not whether the
agent is running. The tmux host has no browser transport.

Every viewer run generates 32 random token bytes and includes their hex encoding in the
`milligram-terminal.v2.<token>` WebSocket subprotocol. The browser must request that exact single
subprotocol. Tokens never belong in URLs or logs. Metadata uses `Cache-Control: no-store` and
`X-Content-Type-Options: nosniff`; no endpoint enables CORS. A connection must also pass the
existing bound-port Host guard and supply exactly one Origin equal to the viewer's localhost
or 127.0.0.1 HTTP origin. Origin and token checks happen before connecting to the host pipe.
This protects against other websites. Loopback HTTP does not authenticate local OS users;
other local processes able to read metadata are outside that browser-origin boundary.

Each accepted browser has one same-user host connection. Binary messages carry terminal bytes
unchanged in each direction, up to 16 KiB per message. Text controls are at most 512 bytes:
`{"type":"resize","columns":80,"rows":24}` and `{"type":"status"}`. Server text messages are
`{"type":"status","pid":123,"clients":1,"columns":80,"rows":24}` and
`{"type":"exited","code":0}`. There are no stop or ring controls on this socket. Existing
guarded viewer actions handle commands. Sizes retain the host protocol's 2–1000 bounds.

The v2 browser protocol bounds unparsed output with a 128 KiB window per connection. After its
terminal parser consumes bytes, the browser sends `{"type":"ack","bytes":16384}` for that many
bytes; these acknowledgements never reach the host. Counts must be positive and no greater
than the outstanding bytes. A full window pauses this client's relay and applies the existing
host backpressure; it does not pause other clients. A client that cannot make progress within
three seconds is disconnected. Terminal writes must acknowledge from their completion callback,
not merely on receipt of a WebSocket message. This bounds queues that socket writes cannot see.

Fragmented messages allow at most 128 receive chunks and have three seconds to finish after
their first chunk. Idle connections remain open. Pipe greeting, individual pipe/socket writes
and close handshakes also have three-second deadlines. The relay has no unbounded output queue;
the host's existing per-client backpressure still applies. Exit follows the last output, then
closes the socket. Detach, broken connections and viewer shutdown release the browser's pipe
without sending stop. Loopback tests exercise all three authorization gates, relay ordering,
concurrent clients, malformed frames, message limits, deadlines and shutdown.

## Browser panel

The bottom dock and `/agent.html` use the same `AgentPanel` module and host session. Vendored
xterm 6.0.0 and fit 0.11.0 need no JavaScript build or remote assets. Each attachment creates
a fresh parser before replay; parsed-output callbacks return credit only to their own socket.
Collapse, page hiding and page exit detach. Retry delays grow from 500 ms to 8 seconds, and
metadata refresh supplies the current in-memory token. A change in project root requires a
page reload instead of silently attaching to a different project on a reused port.

The page bounds pending input at 256 KiB and sends it in 16 KiB frames. Oversized paste is
rejected as a whole. OSC 52 clipboard requests are swallowed; OSC 8 links require Ctrl/Cmd+click,
an http(s) URL and confirmation of the actual destination. Terminal text never becomes HTML.
File links support C# paths and line numbers, including wrapping and wide terminal cells;
source/editor access still passes the server's project containment check, including nested
filesystem links. An explicitly linked project root defines the boundary; nested targets outside
it are rejected before they are followed. Ancestor aliases such as macOS `/var` resolve back to
that boundary; the alias walk never descends into an ordinary outside directory or switches
filesystem roots to probe another drive or network share. Windows comparisons use each component's
stored spelling, preserving exact names when case-sensitive siblings exist and rejecting ambiguous matches.
This check is not an atomic defense against a local
process changing directory entries between checking and opening a file. Responses forbid
framing with `Content-Security-Policy: frame-ancestors 'none'`.

Start, stop and restart use the guarded action endpoint and serialize within the viewer.
Restart checks policy and prerequisites before stopping. Stop remains available for a running
session even if its startup prerequisites disappear. The inspector's selection and message
mailbox remains independent of terminal input.

The separate [browser fixture](tests/Milligram.Browser.Integration/README.md) exercises the
real viewer, relay, pipe and session with a deterministic terminal peer. Its five-browser CI
workflow is separate from the fast test suite and from the native PTY fixture. WebKit does
not establish Safari acceptance; real Copilot, IME and OS clipboard behavior still need hands-on
validation before changing the default host.

## Rollout requirements

The native host remains opt-in. [Issue #26](https://github.com/jhnoor/milligram/issues/26)
requires green cross-platform CI, the end-to-end viewer smoke test, an independent WebSocket
security review and several weeks of daily use without regressions compared with tmux.
Dogfooding must include Linux, macOS and Windows with an older .NET Framework codebase.
Record the eventual decision to retain or retire tmux in that issue before changing the default.

Automated native and browser fixtures provide reproducible evidence for their stated cases.
They do not replace authenticated Copilot interaction, WSL, physical terminal applications,
classic Windows console, Safari, IME or operating-system clipboard validation. Legacy build,
coverage and mutation remain separate work documented in [LEGACY.md](LEGACY.md).
