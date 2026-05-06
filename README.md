# SyncAudio — Blazor + SignalR synchronized audio playback

Plays audio simultaneously across multiple phones/devices in the same group.

## Requirements
- .NET 10 SDK
- An audio file at `wwwroot/audio/track.mp3` (place your own)

## Run
```bash
dotnet run --urls https://0.0.0.0:5001
```

Open `https://<your-LAN-ip>:5001` on each phone, type the same group name, hit Join, wait for ≥25% load, then anyone presses **▶ Play All**.

## How sync works
1. **Clock sync (NTP-style)**: each client pings the hub 12 times, takes the median offset of the 6 lowest-RTT samples.
2. **Scheduled playback**: hub picks `playAt = serverNow + 3s` and broadcasts. Each client converts to its local clock and uses `AudioBufferSourceNode.start(when)` for sample-accurate scheduling.
3. **Preload gate**: button enables at ≥25% download; the 3s lead-time guarantees full decode before playback.

## Notes
- All phones must be on the same network (Wi-Fi recommended; cellular adds variable latency).
- iOS Safari requires HTTPS and a user gesture before audio can play — the Join button satisfies this.
- For HTTPS: `dotnet dev-certs https --trust` or use a real certificate.
- If sync is poor on slow networks, increase `leadSeconds` in the hub from 3.0 to 5.0.
