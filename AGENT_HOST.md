# Agent host protocol (in development)

This branch builds the opt-in host tracked in #22. The default companion remains tmux.
The framing and replay primitives are implemented first; process ownership and the client are
not yet connected. This document describes their wire contract, not a released host.

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
| status | 7 | Empty request; JSON response (defined with the host implementation) |
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

A redraw after attachment, bounded client queues, same-user pipe permissions, process-tree cleanup,
detachment, discovery and real-terminal integration checks remain part of the host implementation.
The six-platform terminal evidence and Windows loader workaround are on the separate
[`codex/pty-spike` branch](https://github.com/jhnoor/milligram/tree/codex/pty-spike/spikes/PtyProbe).
Real Copilot login, xterm.js rendering, WSL and the rollout criteria remain separate acceptance work.
