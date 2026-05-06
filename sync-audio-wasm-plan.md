# Implementation Plan: Synchronized Multi-Device Audio Playback (Blazor WebAssembly)

## Context for Claude Code

You are migrating an existing Blazor Server implementation of synchronized audio playback to Blazor WebAssembly (.NET 10). The feature plays the same audio file simultaneously on PC and mobile with sub-frame sync. The existing server-side version exists as reference — reuse the SignalR hub, message contracts, and JS audio module where possible. The fundamental change: clients are now thick (WASM runs locally), so latency-sensitive logic moves off the server.

## Goals & Non-Goals

**Goals:** <20ms sync on LAN, <80ms on mixed networks, zero playback gaps, automatic drift correction, graceful client join/leave during playback.

**Non-goals:** streaming live audio (file-based only for v1), >2 audio tracks mixed per client, DRM, offline playback.

---

## Phase 1: Solution Restructure

Create three projects:

- `SyncAudio.Client` — Blazor WASM, .NET 10
- `SyncAudio.Server` — ASP.NET Core host for SignalR + static file serving
- `SyncAudio.Shared` — DTOs, message contracts, time-sync math

Move all existing message contracts from the Server project into Shared. The Server project becomes thin — it serves the WASM bundle, hosts the SignalR hub, and serves audio files with proper `Accept-Ranges` headers.

Configure WASM trimming carefully: SignalR client and MessagePack need `<TrimmerRootAssembly>` entries or you'll get runtime "type not found" errors. Enable AOT compilation (`<RunAOTCompilation>true</RunAOTCompilation>`) — the time-sync math runs in a hot loop and interpreted WASM adds ~3–5ms jitter.

---

## Phase 2: NuGet Dependencies

**Client project:**
- `Microsoft.AspNetCore.SignalR.Client`
- `Microsoft.AspNetCore.SignalR.Protocols.MessagePack`
- `MessagePack`
- `Microsoft.Extensions.Logging`

**Server project:**
- `Microsoft.AspNetCore.SignalR`
- `Microsoft.AspNetCore.SignalR.Protocols.MessagePack`
- `Microsoft.Extensions.Hosting`
- Optionally `NAudio` (server-side only, if you need format conversion)

Pin SignalR client and server to the same version. Verify MessagePack serialization works under WASM AOT — some reflection-based resolvers fail; use `[MessagePackObject]` attributes with explicit keys rather than the contractless resolver.

---

## Phase 3: Time Synchronization (the critical part)

Implement a Cristian-style NTP handshake in `SyncAudio.Shared/TimeSync/ClockSynchronizer.cs`:

- Client sends `T1 = clientNow`
- Server responds with `T2 = serverNow`
- Client receives at `T3 = clientNow`
- Round-trip = `T3 - T1`
- Offset = `T2 - (T1 + T3) / 2`

Run 8 samples, discard top/bottom 2, take the median offset of the remaining 4. Re-sync every 30 seconds during idle, every 5 seconds during playback.

**Critical WASM gotcha:** `DateTime.UtcNow` resolution in browser WASM is throttled to 1ms (or worse with Spectre mitigations). Use `performance.now()` via JS interop for sub-millisecond resolution — wrap it in `IHighResolutionClock` so you can swap implementations. The clock JS module should be loaded once at startup as an ES6 module via `JSImportAttribute` (.NET 10 JS interop) rather than `IJSRuntime.InvokeAsync` per call — interop overhead is ~0.1ms per call, which dominates if you call it in a tight loop.

---

## Phase 4: Audio Pipeline Refactor

The existing JS audio module likely lives in the Server project's wwwroot — move it to `SyncAudio.Client/wwwroot/js/audio-engine.js`. Refactor it to expose a clean API:

- `init()`
- `loadBuffer(url)`
- `scheduleStart(virtualTime)`
- `getPosition()`
- `adjustRate(rate)`
- `stop()`

Use `[JSImport]` and `[JSExport]` (the .NET 10 way) instead of `IJSRuntime` for hot-path calls — `getPosition()` will be called 10–20Hz during drift monitoring. Mark the audio module with `<script type="module">` and use `JSHost.ImportAsync` for loading.

For audio loading: use `fetch` + `arrayBuffer` + `AudioContext.decodeAudioData` in JS rather than streaming bytes through Blazor — moving 5MB of audio across the JS-WASM boundary is wasteful and slow. Blazor only needs to know "buffer ready" / "buffer failed".

**Pre-buffer rule:** never call `scheduleStart` until every participating client has reported `bufferReady`. The server-side coordinator tracks readiness and only then issues the `PlayAt(virtualTime)` broadcast with `virtualTime = serverNow + 500ms` (tune this — it must exceed your worst-case round-trip + decode + scheduling slack).

---

## Phase 5: SignalR Hub & Coordinator

**Hub methods (client → server):**
- `RegisterClient(deviceInfo)`
- `ReportClockSync(samples)`
- `ReportBufferReady(trackId)`
- `ReportPosition(virtualTime, audioPosition)`
- `RequestPlay(trackId)`
- `RequestStop()`

**Server-to-client:**
- `LoadTrack(url, trackId)`
- `PlayAt(virtualTime, trackId)`
- `StopAt(virtualTime)`
- `AdjustRate(rate)`
- `Reseek(virtualTime, audioPosition)`

The `PlaybackCoordinator : BackgroundService` holds the room state: connected clients, their clock offsets, their reported positions. Every 2 seconds during playback it computes drift per client (expected position vs. reported), and issues `AdjustRate(1.001)` or `AdjustRate(0.999)` for drift <50ms, or hard `Reseek` for >50ms. Use `Channel<T>` for the coordinator's command queue rather than locks.

**WASM-specific:** SignalR over WebSockets only (disable long-polling fallback) — long-polling jitter destroys sync. Set `KeepAliveInterval = 5s`, `ClientTimeoutInterval = 15s`. Handle reconnection: on reconnect, the client must re-run clock sync from scratch before rejoining playback.

---

## Phase 6: UI & State Management

Build three Razor components:

- `RoomLobby` — join/create room, see connected devices
- `AudioController` — play/stop/track selection, host-only
- `SyncStatus` — per-client offset, drift, buffer state (diagnostic)

State lives in a singleton `PlaybackState` service injected into components; it subscribes to hub events and raises `StateHasChanged` via `InvokeAsync`.

Don't render the current playback position from C# state at 60fps — that thrashes the WASM render loop. Use a JS-driven progress bar that reads `audioContext.currentTime` directly and updates the DOM, or accept 4Hz updates from C#.

---

## Phase 7: Testing & Validation

Write a `SyncHarness` page that opens two `AudioContext`s in the same browser tab and runs the full protocol against itself — useful for local debugging without two devices.

For real measurement: play a click track simultaneously on two devices, record both with a third device's microphone, measure the offset in Audacity. Target <20ms peak-to-peak over a 60-second test.

Test deliberately bad conditions:
- Throttle one client to 3G in DevTools
- Kill and restore WiFi mid-playback
- Join a third client mid-song

---

## Phase 8: Production Hardening

Add Application Insights or OpenTelemetry on the server for coordinator metrics (clients per room, mean drift, reseek rate). Audio file serving should use `Microsoft.AspNetCore.StaticFiles` with response caching and range requests. Consider a CDN if clients are geographically distributed — the audio download is the largest single source of "client not ready" timeouts.

---

## Migration Order (suggested)

1. **Phase 1–2** in one PR (compiles, runs empty)
2. **Phase 3** in isolation with a test page that just shows clock offset (verify <2ms stability before moving on)
3. **Phase 4** with a single-client "play locally" mode
4. **Phase 5–6** together — this is where the multi-client behavior emerges
5. **Phase 7–8** ongoing

---

## Known WASM Risks to Flag Early

- **iOS Safari user gesture:** `AudioContext` requires a user gesture to start — your "join room" button must be the gesture
- **Background tab throttling:** mobile browsers throttle `AudioContext` and SignalR in background tabs — document as known limitation
- **iOS Low Power Mode:** disables Web Audio scheduling precision; detect and warn
- **Firefox `decodeAudioData`:** historically slower than Chromium — pre-decode on a worker if you support it
