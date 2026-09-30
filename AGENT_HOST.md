# Agent host protocol (in development)

This branch builds the opt-in host tracked in #22. The default companion remains tmux.
The framing, replay, terminal session and same-user pipe transport are implemented first;
process ownership and companion integration are not yet connected. This document describes
their wire contract, not a released host.

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

Only input, resize, ring, stop and empty status requests are accepted after greeting. Invalid
frames, incomplete frames, unexpected server-only messages and transport failures disconnect
that client. Each connection has one output task and serialized client writes. A slow connection
inherits the session's bounded queue and cancellation, so it cannot block accepting new clients.
Host shutdown stops new connections and lets existing clients receive output followed by exit
before closing them; cancellation can interrupt idle readers and the listener.

Process-tree cleanup, detachment, discovery, companion integration and real-terminal checks
remain part of the host implementation.
The six-platform terminal evidence and Windows loader workaround are on the separate
[`codex/pty-spike` branch](https://github.com/jhnoor/milligram/tree/codex/pty-spike/spikes/PtyProbe).
Real Copilot login, xterm.js rendering, WSL and the rollout criteria remain separate acceptance work.
