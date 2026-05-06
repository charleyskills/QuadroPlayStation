// Sync engine — fires sample-accurate playback against the audio thread.
//
// Sync model:
//   1. The hub broadcasts a wall-clock timestamp (serverNow + LEAD_SECONDS).
//   2. ClockSync (NTP-style) gives us serverNow → localNow offset.
//   3. We map the wall-clock target → audioContext.currentTime once at fire
//      time, then call AudioBufferSourceNode.start(when). The browser fires
//      it on the audio thread, sample-accurately against the audio clock.
//
// Web Audio replaces the older HTML5 <audio> + setTimeout(audio.play()) path
// because that path could not break a ~50–100 ms cross-device drift floor on
// mobile (audio.play() startup latency is opaque, setTimeout runs on the JS
// event loop).
//
// External API (consumed by NowPlaying.razor — do not break):
//   initialize, setSrc, setVolume, connectAndJoin,
//   setChannelMap, setLatencyOffset, adjustLatency, requestPlay, requestStop
const engine = {
  // ─── Public state ──────────────────────────────────────────────────
  ctx: null,
  conn: null,
  dotnet: null,
  audioUrl: null,
  group: null,
  offset: 0,
  progress: 0,
  channels: 0,
  channelMap: { left: 0, right: 1 },
  playTimer: null,

  // ─── Web Audio internal state ──────────────────────────────────────
  // Cached, fully-decoded buffer for audioUrl. Decode is async (100–500 ms
  // on mobile); we hide the latency by decoding ahead of requestPlay() and
  // gating the UI on UpdateReady.
  _buffer: null,
  _decodingUrl: null,        // single-flight gate by URL
  _decodePromise: null,
  _gain: null,               // user-volume node, persistent across plays
  // Active AudioBufferSourceNode. Single-shot — we rebuild it on every
  // schedulePlay / latency-adjust.
  _source: null,
  // Audio-clock anchor: position = ctx.currentTime - _ctxStartTime + _bufferOffset.
  _ctxStartTime: 0,
  _bufferOffset: 0,
  _isPlaying: false,
  _positionTimer: null,
  _wakeLock: null,

  // Per-device manual latency offset (ms). Persisted in localStorage.
  // Auto-compensation handles outputLatency; this closes any residual gap.
  _manualLatencyOffsetMs: 0,

  // Last buffer fraction reported to the hub. Cached so that connectAndJoin
  // can replay the current value once the SignalR connection is up — without
  // this, a member that finished decoding before joining would look like 0%
  // to the adaptive-lead calculator.
  _hubProgress: 0,

  // iOS Safari requires a user gesture to unlock AudioContext. Once unlocked
  // via unlockAudio(), ctx.resume() succeeds without a gesture for the rest
  // of the session.
  _audioUnlocked: false,
  // Stores a pending ScheduledPlay timestamp when schedulePlay() fires before
  // the user has unlocked audio (auto-join path on iOS).
  _pendingPlayMs: null,

  async initialize(dotnetRef, audioUrl) {
    this.dotnet = dotnetRef;
    this.audioUrl = audioUrl || null;

    const Ctx = window.AudioContext || window.webkitAudioContext;
    this.ctx = new Ctx({ latencyHint: 'interactive' });

    // Volume sits on a persistent gain node — we can't change a source
    // node's gain after it's started, so every source feeds through here.
    this._gain = this.ctx.createGain();
    this._gain.gain.value = 1.0;
    this._gain.connect(this.ctx.destination);

    try {
      const saved = localStorage.getItem('syncAudio.channelMap.' + audioUrl);
      if (saved) this.channelMap = JSON.parse(saved);
    } catch { /* ignore corrupt entry */ }

    try {
      const savedOffset = localStorage.getItem('syncAudio.latencyOffset');
      if (savedOffset !== null) this._manualLatencyOffsetMs = parseInt(savedOffset, 10) | 0;
    } catch { /* ignore */ }
    this._invoke('UpdateLatencyOffset', this._manualLatencyOffsetMs);

    // Pre-decode if we already have a URL. Caller may also pass "" and
    // wire the URL in later via setSrc — both flows are supported.
    if (this.audioUrl) {
      this._decode(this.audioUrl).catch(() => { /* errors flow via UpdateReady */ });
    }

    document.addEventListener('visibilitychange', () => {
      if (document.visibilityState === 'visible') {
        // Re-resume the context when returning from background. This works
        // without a fresh gesture because the context was already unlocked.
        if (this._audioUnlocked && this.ctx.state !== 'running') {
          this.ctx.resume().catch(() => {});
        }
        if (this._isPlaying) this._acquireWakeLock();
      }
    });
  },

  async _acquireWakeLock() {
    if (!('wakeLock' in navigator)) return;
    if (!(navigator.maxTouchPoints > 1 || 'ontouchstart' in window)) return;
    try {
      this._wakeLock = await navigator.wakeLock.request('screen');
    } catch { /* denied or feature unavailable */ }
  },

  _releaseWakeLock() {
    if (this._wakeLock) {
      try { this._wakeLock.release(); } catch { /* ignore */ }
      this._wakeLock = null;
    }
  },

  // Must be called from within a user-gesture handler (e.g. Join or tap).
  // Plays a silent 1-sample buffer so iOS marks the context "user-activated"
  // for the rest of the session — future resume() calls succeed without gesture.
  async unlockAudio() {
    if (this._audioUnlocked) return;
    try {
      await this.ctx.resume();
      const buf = this.ctx.createBuffer(1, 1, this.ctx.sampleRate);
      const src = this.ctx.createBufferSource();
      src.buffer = buf;
      src.connect(this.ctx.destination);
      src.start(0);
      this._audioUnlocked = (this.ctx.state === 'running');
    } catch { /* denied — user will be prompted via ShowUnlockPrompt */ }
  },

  // Called by C# when the user taps the "Tap to play" overlay.
  async unlockAndPlay() {
    await this.unlockAudio();
    this._invoke('ShowUnlockPrompt', false);
    if (this._audioUnlocked && this._pendingPlayMs != null) {
      const ms = this._pendingPlayMs;
      this._pendingPlayMs = null;
      this.schedulePlay(ms);
    }
  },

  // Optional callbacks: caller may not implement every JSInvokable. Swallow rejections.
  _invoke(method, ...args) {
    if (!this.dotnet) return;
    try {
      const p = this.dotnet.invokeMethodAsync(method, ...args);
      if (p && typeof p.catch === 'function') p.catch(() => {});
    } catch { /* circuit gone or method missing */ }
  },

  _getDeviceName() {
    try {
      const saved = localStorage.getItem('syncaudio.deviceName');
      if (saved) return saved;
    } catch { }
    const ua = navigator.userAgent;
    if (/iPhone/.test(ua)) return 'iPhone';
    if (/iPad/.test(ua)) return 'iPad';
    if (/Android/.test(ua)) return 'Android';
    if (/Macintosh/.test(ua)) return 'Mac';
    if (/Windows/.test(ua)) return 'PC';
    return 'Device';
  },

  _reportPhase(phase, progress) {
    if (!this.conn || !this.group) return;
    if (this.conn.state !== signalR.HubConnectionState.Connected) return;
    try {
      const p = this.conn.invoke('ReportPhase', this.group, phase, progress ?? 0);
      if (p && typeof p.catch === 'function') p.catch(() => {});
    } catch { /* connection torn down between checks */ }
  },

  // Called from Razor when IsSplitting transitions to true (C# side started ffmpeg).
  setSplitting(isSplitting) {
    if (isSplitting) this._reportPhase('splitting', 0);
    // When splitting ends, setSrc is called next → _reportPhase('buffering') fires there.
  },

  // Forward our buffer-progress sample to the hub so RequestPlay can size the
  // lead time to the slowest peer. No-op if SignalR isn't connected yet — the
  // value is cached on this._hubProgress and replayed by connectAndJoin.
  _reportHubProgress(frac) {
    this._hubProgress = frac;
    if (!this.conn || !this.group) return;
    if (this.conn.state !== signalR.HubConnectionState.Connected) return;
    try {
      const p = this.conn.invoke('ReportProgress', this.group, frac);
      if (p && typeof p.catch === 'function') p.catch(() => {});
    } catch { /* connection torn down between checks */ }
  },

  async _waitForConnected(conn, timeoutMs = 3000) {
    if (conn.state === signalR.HubConnectionState.Connected) return;
    const deadline = Date.now() + timeoutMs;
    while (conn.state !== signalR.HubConnectionState.Connected) {
      if (conn.state === signalR.HubConnectionState.Disconnected ||
          Date.now() >= deadline) {
        throw new Error(`Hub connection not ready for invoke (state: ${conn.state})`);
      }
      await new Promise(r => setTimeout(r, 100));
    }
  },

  // ─── Decode + readiness ────────────────────────────────────────────
  _decode(url) {
    if (this._buffer && this.audioUrl === url && !this._decodingUrl) {
      return Promise.resolve(this._buffer);
    }
    if (this._decodingUrl === url && this._decodePromise) {
      return this._decodePromise;
    }
    this._decodingUrl = url;
    this._invoke('UpdateReady', false);
    this._invoke('UpdateProgress', 0);
    this._reportHubProgress(0);

    this._decodePromise = (async () => {
      try {
        const resp = await fetch(url);
        if (!resp.ok) throw new Error(`fetch ${url} → ${resp.status}`);

        // Stream the body so we can report real download progress.
        // Large DTS/FLAC splits can be 100–300 MB; without this the UI
        // shows nothing until the full download completes.
        const contentLength = parseInt(resp.headers.get('Content-Length') || '0', 10);
        const reader = resp.body.getReader();
        const chunks = [];
        let received = 0;
        let lastReported = 0;
        while (true) {
          const { done, value } = await reader.read();
          if (done) break;
          if (this._decodingUrl !== url) return this._buffer; // cancelled by setSrc
          chunks.push(value);
          received += value.length;
          // Throttle to one invoke per 5% to avoid flooding the circuit.
          // Reserve the final 10% of the 0–1 range for the decode step.
          if (contentLength > 0) {
            const frac = received / contentLength;
            if (frac - lastReported >= 0.05) {
              this._invoke('UpdateProgress', frac * 0.9);
              // Hub gets the raw download fraction (no 0.9 reservation) — it
              // uses this for cross-device lead-time estimation, not local UI.
              this._reportHubProgress(frac);
              lastReported = frac;
            }
          }
        }

        // Reassemble chunks into one contiguous ArrayBuffer for decodeAudioData.
        const merged = new Uint8Array(received);
        let off = 0;
        for (const chunk of chunks) { merged.set(chunk, off); off += chunk.length; }

        // decodeAudioData neuters the input ArrayBuffer; we don't reuse it.
        const buffer = await this.ctx.decodeAudioData(merged.buffer);
        // Drop the result if the user toggled streams while we were decoding.
        if (this._decodingUrl !== url) return buffer;

        this._buffer = buffer;
        this.audioUrl = url;
        this.channels = buffer.numberOfChannels;
        this._invoke('UpdateProgress', 1);
        this._reportHubProgress(1);
        this._invoke('UpdateReady', true);
        this._invoke('UpdateAudioInfo',
          buffer.numberOfChannels,
          this.ctx.sampleRate,
          this.channelMap.left,
          this.channelMap.right);
        console.log(`decoded ${url}: ${buffer.numberOfChannels}ch ${buffer.duration.toFixed(2)}s`);
        return buffer;
      } catch (e) {
        console.error('decode failed', url, e);
        this._invoke('UpdateReady', false);
        throw e;
      } finally {
        if (this._decodingUrl === url) this._decodingUrl = null;
        this._decodePromise = null;
      }
    })();
    return this._decodePromise;
  },

  // ─── Source-graph construction ─────────────────────────────────────
  // AudioBufferSourceNode is single-shot — fresh one per play. The common
  // case is stereo pass-through (L=0, R=1 from a stereo buffer), in which
  // case we skip the splitter→merger detour.
  _createSource(buffer, channelMap) {
    const src = this.ctx.createBufferSource();
    src.buffer = buffer;

    const N = buffer.numberOfChannels;
    const l = Math.max(0, Math.min(N - 1, channelMap.left | 0));
    const r = Math.max(0, Math.min(N - 1, channelMap.right | 0));
    const passthrough = N === 2 && l === 0 && r === 1;

    if (passthrough) {
      src.connect(this._gain);
    } else {
      const splitter = this.ctx.createChannelSplitter(N);
      const merger = this.ctx.createChannelMerger(2);
      src.connect(splitter);
      splitter.connect(merger, l, 0);
      splitter.connect(merger, r, 1);
      merger.connect(this._gain);
    }
    return src;
  },

  setSrc(url) {
    if (!url) return;
    if (this.audioUrl === url && this._buffer) return;

    // Stop in-flight playback before swapping the buffer — otherwise the
    // running source would keep feeding the speakers from the old file.
    this._stopSource();
    this.audioUrl = url;
    this._buffer = null;
    this._bufferOffset = 0;
    this._reportPhase('buffering', 0);
    this._decode(url).catch(() => { /* errors flow via UpdateReady */ });
  },

  setVolume(value) {
    if (!this._gain) return;
    const v = Math.max(0, Math.min(1, Number(value) || 0));
    this._gain.gain.value = v;
  },

  async connectAndJoin(group) {
    this.group = group;
    // Attempt to unlock the AudioContext. Succeeds immediately when called
    // from a user-gesture (manual Join); silently no-ops for auto-join (iOS
    // rejects resume() without a gesture — _audioUnlocked stays false and
    // schedulePlay() will ask the user to tap when PLAY arrives).
    await this.unlockAudio();

    if (typeof signalR === 'undefined') {
      throw new Error('signalR client failed to load — check /lib/signalr/signalr.min.js is being served');
    }

    // Drop any existing connection before creating a new one to avoid orphaned
    // event handlers holding a server-side group slot.
    if (this.conn) {
      try { await this.conn.stop(); } catch {}
      this.conn = null;
    }

    // Build the connection into a local variable first. Assigning to this.conn
    // only after start() succeeds prevents a concurrent connectAndJoin call
    // from overwriting this.conn while we are still mid-handshake.
    const conn = new signalR.HubConnectionBuilder()
      .withUrl('/synchub').withAutomaticReconnect().build();

    conn.on('ScheduledPlay', (t, trackId) =>
      this.dotnet.invokeMethodAsync('OnScheduledPlay', t, trackId ?? ''));
    conn.on('StopPlay', () => this.stop());
    conn.on('MemberCount', n => this.dotnet.invokeMethodAsync('UpdateMembers', n));
    conn.on('AlbumSelected', rk => this.dotnet.invokeMethodAsync('OnRemoteAlbumSelected', rk));
    conn.on('TrackSelected', trackId => {
      this.dotnet.invokeMethodAsync('OnRemoteTrackSelected', trackId)
        .then(isSameTrack => {
          // Already buffered for this track — re-announce readiness so the hub
          // can fire the pending play once all peers confirm.
          if (isSameTrack && this._warm) this._reportHubProgress(1.0);
        })
        .catch(() => {});
    });
    conn.on('TrackSelectedWithMeta', (trackId, albumKey, title, artist, coverUrl) => {
      this.dotnet.invokeMethodAsync(
        'OnRemoteTrackSelectedWithMeta', trackId, albumKey, title, artist, coverUrl)
        .catch(() => {});
    });
    // Hub fires this when adaptive lead hit the 30s cap — peer is too slow
    // to be ready in time. Caller-only: surfaced to the user who hit Play.
    conn.on('WaitingForPeers', (remainingMs, fraction) =>
      this._invoke('UpdateWaitingForPeers', remainingMs | 0, fraction));
    conn.on('PeersStateUpdated', peers => {
      const myId = conn.connectionId;
      const annotated = peers.map(p => ({ ...p, isSelf: p.connectionId === myId }));
      this._invoke('UpdatePeersState', annotated);
    });

    // Kick off the decode now (if not already in flight) so the buffer is
    // ready before the first ScheduledPlay broadcast lands.
    if (this.audioUrl) {
      this._decode(this.audioUrl).catch(() => {});
    }

    await conn.start();
    // withAutomaticReconnect can transition the state to Reconnecting immediately
    // after start() resolves if the underlying transport flapped. Treat this as a
    // hard failure so the C# retry in HandleJoinInterop can kick in cleanly.
    if (conn.state !== signalR.HubConnectionState.Connected) {
      throw new Error(`Hub did not reach Connected state (got: ${conn.state})`);
    }
    this.conn = conn;
    await this.syncClock();
    await this._waitForConnected(this.conn, 3000);
    await this.conn.invoke('JoinGroup', group, this._getDeviceName());
    // Replay our last buffer progress so the hub's adaptive-lead calculator
    // doesn't see a freshly-joined member as 0%.
    this._reportHubProgress(this._hubProgress);
    // Replay now-playing metadata so peers see our current track after reconnect.
    if (this._currentTrackMeta) {
      const m = this._currentTrackMeta;
      this.reportNowPlaying(m.trackId, m.title, m.artist, m.coverUrl);
    }
  },

  async syncClock() {
    const conn = this.conn;
    const samples = [];
    for (let i = 0; i < 12; i++) {
      await this._waitForConnected(conn, 3000);
      const t1 = Date.now();
      const tServer = await conn.invoke('Ping');
      const t4 = Date.now();
      samples.push({ rtt: t4 - t1, off: tServer - (t1 + t4) / 2 });
      await new Promise(r => setTimeout(r, 80));
    }
    samples.sort((a, b) => a.rtt - b.rtt);
    const best = samples.slice(0, 6).map(s => s.off).sort((a, b) => a - b);
    this.offset = best[Math.floor(best.length / 2)];
    await this.dotnet.invokeMethodAsync('UpdateOffset', Math.round(this.offset));
  },

  // ─── Channel routing ───────────────────────────────────────────────
  // Mostly vestigial since the server-side TrackSplit produces stereo MP3s
  // (L=0, R=1), but kept so the channel-map UI in the legacy state still
  // works when the source is a multi-channel file.
  applyChannelMap(map) {
    this.channelMap = {
      left: map.left | 0,
      right: map.right | 0,
    };
    try {
      localStorage.setItem('syncAudio.channelMap.' + this.audioUrl, JSON.stringify(this.channelMap));
    } catch { /* quota / private mode — fine */ }

    if (this._buffer) {
      this._invoke('UpdateAudioInfo',
        this._buffer.numberOfChannels,
        this.ctx.sampleRate,
        this.channelMap.left,
        this.channelMap.right);
    }
    // Channel-map changes take effect at the next schedulePlay. Live
    // remap mid-stream isn't supported (would need a source swap, glitchy).
  },

  setChannelMap(left, right) {
    this.applyChannelMap({ left, right });
  },

  // ─── Sync configuration ────────────────────────────────────────────
  // Lead time: how many seconds the server pushes the play-at moment into
  // the future. Long enough to absorb cross-client decode + scheduling
  // jitter, short enough that the user doesn't notice. 5s is needed because
  // large files streamed via HTTP range requests can take >3s on slower
  // mobile networks.
  LEAD_SECONDS: 5.0,

  // ─── Pre-warm ──────────────────────────────────────────────────────
  // With Web Audio, "warm" means the buffer is decoded.
  get _warm() { return !!this._buffer; },

  async _prewarm() {
    if (!this.audioUrl) return false;
    if (this._buffer) return true;
    try {
      await this._decode(this.audioUrl);
      return !!this._buffer;
    } catch {
      return false;
    }
  },

  setLatencyOffset(ms) {
    const v = (ms | 0);
    this._manualLatencyOffsetMs = v;
    try { localStorage.setItem('syncAudio.latencyOffset', String(v)); } catch { /* private mode */ }
    this._invoke('UpdateLatencyOffset', v);
  },

  saveCurrentTrack(trackId, source, ratingKey) {
    try {
      localStorage.setItem('syncAudio.currentTrack',
        JSON.stringify({ trackId, source, ratingKey: ratingKey ?? null }));
    } catch { /* quota / private mode */ }
  },

  loadCurrentTrack() {
    try {
      const raw = localStorage.getItem('syncAudio.currentTrack');
      return raw ? JSON.parse(raw) : null;
    } catch { return null; }
  },

  saveStreamPair(pair) {
    try { localStorage.setItem('syncAudio.streamPair', pair); } catch { /* private mode */ }
  },

  loadStreamPair() {
    try { return localStorage.getItem('syncAudio.streamPair'); } catch { return null; }
  },

  // Adjust the offset AND, if currently playing, immediately reflect it
  // by replacing the source with a fresh one at the shifted position.
  // A 10–50 ms seek is below most click-noticeable thresholds.
  adjustLatency(deltaMs) {
    const d = deltaMs | 0;
    if (d === 0) return;
    this.setLatencyOffset(this._manualLatencyOffsetMs + d);

    if (this._isPlaying && this._source && this._buffer) {
      const now = this.ctx.currentTime;
      const elapsed = Math.max(0, now - this._ctxStartTime);
      // +d ms = "play earlier on this device" → skip forward in the buffer.
      const newOffset = Math.max(0, Math.min(
        this._buffer.duration,
        this._bufferOffset + elapsed + d / 1000));
      this._stopSource();
      this._startSourceAt(now + 0.005, newOffset);
    }
  },

  // ─── Playback core ─────────────────────────────────────────────────
  schedulePlay(playAtServerMs) {
    if (this.playTimer) { clearTimeout(this.playTimer); this.playTimer = null; }

    // iOS Safari only allows AudioContext.resume() inside a user-gesture handler.
    // If the context was never unlocked (auto-join path), store the target time
    // and ask C# to show the "Tap to play" overlay. The user's tap calls
    // unlockAndPlay(), which unlocks and re-enters schedulePlay().
    if (!this._audioUnlocked) {
      this._pendingPlayMs = playAtServerMs;
      this._invoke('ShowUnlockPrompt', true);
      return;
    }

    // Context may have been re-suspended (e.g. app backgrounded). Resume it;
    // the 5-second lead window gives the promise time to settle before src.start().
    if (this.ctx.state !== 'running') {
      this.ctx.resume().catch(() => {});
    }

    if (!this._buffer) {
      // Decode wasn't ready when the broadcast arrived. Recurse once the
      // buffer is in place; if the wall-clock target is already past at
      // that point, the math below clamps to "play immediately".
      this._decode(this.audioUrl)
        .then(() => this.schedulePlay(playAtServerMs))
        .catch(() => {});
      return;
    }

    this._stopSource();

    // Wall-clock target on this device (Date.now() basis).
    const playAtLocalMs = playAtServerMs - this.offset;

    // Bridge wall-clock → audio-clock: at this instant ctx.currentTime
    // and Date.now() advance together, so for any future wall-clock
    // target the equivalent audio-clock is ctx.currentTime + Δ/1000.
    const nowMs = Date.now();
    const ctxNow = this.ctx.currentTime;
    const outputLatencySec = (this.ctx.outputLatency || 0);
    const manualOffsetSec = this._manualLatencyOffsetMs / 1000;

    // Aim audio-clock fire moment AT the wall-clock target, minus device
    // output latency, minus user manual offset (positive = play earlier).
    const targetCtxTime = ctxNow + (playAtLocalMs - nowMs) / 1000
      - outputLatencySec - manualOffsetSec;

    // Clamp to "very-near future" so a missed slot still plays — matches
    // the legacy "if delayMs <= 0, fire()" behaviour.
    const fireAt = Math.max(ctxNow + 0.005, targetCtxTime);
    this._startSourceAt(fireAt, this._bufferOffset);

    const leadMs = (targetCtxTime - ctxNow) * 1000;
    console.log(`scheduled ctxTime=${fireAt.toFixed(4)} (lead=${leadMs.toFixed(0)}ms, outputLatency=${(outputLatencySec * 1000).toFixed(1)}ms, manual=${this._manualLatencyOffsetMs}ms)`);
  },

  _startSourceAt(ctxTargetTime, bufferOffsetSec) {
    if (!this._buffer) return;
    const src = this._createSource(this._buffer, this.channelMap);
    src.onended = () => {
      // Fires for both natural end-of-buffer AND explicit src.stop().
      // Only flip UI to paused if this source is still the active one;
      // _stopSource() nulls the handler before stopping to suppress this.
      if (this._source === src) {
        this._isPlaying = false;
        this._source = null;
        this._invoke('UpdatePlayState', false);
        this._invoke('UpdateTrackEnded');
        this._stopPositionTimer();
        this._releaseWakeLock();
      }
    };
    src.start(ctxTargetTime, bufferOffsetSec);
    this._source = src;
    this._ctxStartTime = ctxTargetTime;
    this._bufferOffset = bufferOffsetSec;
    this._isPlaying = true;
    this._invoke('UpdatePlayState', true);
    this._reportPhase('playing', 1.0);
    this._startPositionTimer();
    this._acquireWakeLock();
  },

  _stopSource() {
    if (this._source) {
      try { this._source.onended = null; } catch { /* ignore */ }
      try { this._source.stop(); } catch { /* already ended */ }
      try { this._source.disconnect(); } catch { /* ignore */ }
      this._source = null;
    }
    if (this._isPlaying) {
      this._isPlaying = false;
      this._invoke('UpdatePlayState', false);
      this._reportPhase(this._buffer ? 'ready' : 'idle', this._hubProgress);
    }
    this._stopPositionTimer();
    this._releaseWakeLock();
  },

  _startPositionTimer() {
    this._stopPositionTimer();
    // 200 ms cadence matches the legacy timeupdate throttle.
    this._positionTimer = setInterval(() => this._reportPosition(), 200);
  },

  _stopPositionTimer() {
    if (this._positionTimer) {
      clearInterval(this._positionTimer);
      this._positionTimer = null;
    }
  },

  _reportPosition() {
    if (!this._buffer || !this._isPlaying) return;
    const elapsed = Math.max(0, this.ctx.currentTime - this._ctxStartTime);
    const position = Math.min(this._buffer.duration, this._bufferOffset + elapsed);
    this._invoke('UpdatePosition', position, this._buffer.duration);
  },

  stop() {
    if (this.playTimer) { clearTimeout(this.playTimer); this.playTimer = null; }
    this._stopSource();
    this._bufferOffset = 0;
  },

  async requestPlay(trackId) {
    // Defensive: if the user bypassed the UI gate, don't ask the hub to schedule until we're
    // warm. Otherwise, this client lags every other group member.
    if (!this._warm) {
      try { await this._prewarm(); } catch { /* fall through; play may stall */ }
    }
    return this.conn.invoke('RequestPlay', this.group, trackId ?? '', this.LEAD_SECONDS);
  },
  requestStop() { return this.conn.invoke('RequestStop', this.group); },

  selectAlbum(albumRatingKey) {
    if (!this.conn || !this.group) return;
    return this.conn.invoke('RequestSelectAlbum', this.group, albumRatingKey);
  },

  selectTrack(trackId) {
    if (!this.conn || !this.group) return;
    return this.conn.invoke('RequestSelectTrack', this.group, trackId);
  },

  selectTrackWithMeta(trackId, albumKey, title, artist, coverUrl) {
    if (!this.conn || !this.group) return;
    return this.conn.invoke('RequestSelectTrackWithMeta',
      this.group, trackId ?? '', albumKey ?? '',
      title ?? '', artist ?? '', coverUrl ?? '');
  },

  reportNowPlaying(trackId, title, artist, coverUrl) {
    this._currentTrackMeta = { trackId, title, artist, coverUrl };
    if (!this.conn || !this.group) return;
    if (this.conn.state !== signalR.HubConnectionState.Connected) return;
    try {
      const p = this.conn.invoke('ReportNowPlaying', this.group,
        trackId ?? '', title ?? '', artist ?? '', coverUrl ?? '');
      if (p && typeof p.catch === 'function') p.catch(() => {});
    } catch { /* connection torn down */ }
  },

  getViewportWidth() {
    return window.innerWidth;
  },
};
window.syncAudio = engine;
