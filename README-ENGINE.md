# AirStream.Engine integration

AirStream now contains a separate Rust process at `src/AirStream.Engine`. The WPF application starts it automatically and communicates over newline-delimited JSON on stdin/stdout.

## Requirements

- Windows 10/11 x64
- .NET 10 SDK
- Rust stable toolchain (`rustup`) with the MSVC target
- Visual C++ Build Tools and a Windows SDK (Desktop development with C++)

## Build

From the repository root:

```powershell
dotnet build .\AirStream.slnx
```

The WPF project runs `cargo build --release` and copies the resulting `airstream-engine.exe` beside the WPF application as `AirStream.Engine.exe`. `dotnet publish` also copies the engine into the publish directory.

To build the Rust engine by itself:

```powershell
cd .\src\AirStream.Engine
cargo build --release
```

## IPC protocol (initial version)

- One JSON object per line.
- Engine messages are written to stdout only.
- Diagnostics are written to stderr.
- `{"type":"ping","request_id":"..."}` returns `pong`.
- `{"type":"shutdown"}` returns `engine_stopping` and exits.

The WPF app starts the engine at window startup, sends an initial ping, and shuts it down when the application exits. Engine startup failure is logged to the Visual Studio Code debug output and does not disable the existing discovery or local WASAPI capture functionality.

## Current scope

This is process/IPC integration only. It does **not** yet implement AirPlay pairing, RTSP, PTP, RTP, ALAC encoding, or HomePod playback. The existing C# WASAPI capture still operates as before; it has not yet been moved into Rust.

## Licensing

The newly added Rust engine scaffold declares MIT and uses `serde` / `serde_json`, which offer permissive licensing. Review all dependency licenses before distributing release binaries.


## Rust WASAPI capture (initial implementation)

The native engine now accepts `start_capture` and `stop_capture` IPC commands. On Windows it captures the default render endpoint in WASAPI shared loopback mode and reports capture format and byte counters over JSON Lines. Audio sample payloads are not yet forwarded to an AirPlay transport; the current capture implementation is a hardware validation milestone.

The Windows-only `wasapi` dependency is version 0.27. The capture thread initializes and deinitializes COM on its own thread.

IPC examples:

```json
{"type":"start_capture"}
{"type":"stop_capture"}
```

Expected events include `capture_starting`, `capture_started`, `capture_stats`, and `capture_stopped`.

AirPlay 2 pairing, encrypted control sessions, RTSP setup, ALAC packetization, RTP transport, and PTP synchronization are not implemented yet.
