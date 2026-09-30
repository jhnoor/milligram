# Agent host protocol (in development)

This branch builds the opt-in host tracked in #22. The default companion remains tmux.
Framing, replay, the terminal session, same-user pipes, project lease, detached host and companion
control are implemented. `agent.host: "milligram"` selects the native host for session commands;
interactive attachment is still being built. This document describes a development wire contract.

## Framing

A frame is a four-byte little-endian signed length, a one-byte kind, then its payload.
The length includes the kind and must be between 1 and 16,385. End of stream between frames is
a clean disconnect; an incomplete header or body is an error. Unknown kinds and invalid control
payloads are rejected. Each connection has one writer to prevent interleaved frames.

| Kind | Byte | Payload |
|---|---|---|
| hello | 1 | JSON: `protocol` (positive integer), `version` (Milligram version, 1–128 characters); at most 512 bytes |
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
`endpoint`, `protocol`, Milligram `version` and `started` timestamp. The lease removes only the
exact record it published, before releasing its lock. Repeated disposal cannot affect a new
owner. Missing, unreadable or malformed discovery is read as absent (including a Windows file
pending deletion); liveness must still be checked by
connecting, never inferred from a PID in this file.

Pipe names are `milligram-` followed by 20 ASCII hexadecimal characters derived from the full
project root. Trailing separators are normalized, as is case on Windows. The host log path is
`.milligram/run/agent-host.log`.

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

`tests/Milligram.AgentHost.Integration` is a standalone fake-agent program, outside the fast
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
Startup reports ownership only when its own live child published discovery. A concurrent caller
reuses the winning host and cleans up its losing child, so two viewers cannot both acquire
shutdown ownership. Tmux reports creation separately from reuse through the same companion port.

A notification sends ring followed by status and waits for the status response, ensuring the
doorbell has been submitted. Stop drains the pipe through EOF and waits for the old owner's
discovery or lease to be released; it also handles a host that is still initializing. Malformed
greetings are not treated as running hosts, and incompatible protocol versions require restart.
Selection is fixed for each viewer run after policy loading. Changing `agent.host` requires a
viewer restart; stop an old session before switching hosts. User-facing terminal attachment
remains part of the implementation.
The six-platform terminal evidence and Windows loader workaround are on the separate
[`codex/pty-spike` branch](https://github.com/jhnoor/milligram/tree/codex/pty-spike/spikes/PtyProbe).
Real Copilot login, xterm.js rendering, WSL and the rollout criteria remain separate acceptance work.
