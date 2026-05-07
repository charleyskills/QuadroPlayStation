using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Caching.Memory;
using SyncAudio.Client.Components.Pages.NowPlaying.StateMachines;
using SyncAudio.Core.Models;
using SyncAudio.Core.Services;
using SyncAudio.Core.Services.TrackSplit;

namespace SyncAudio.Client.Components.Pages.NowPlaying;

public sealed class NowPlayingLogic(
    ITrackLibraryService library,
    ITrackSplitService splitter,
    ILogger<NowPlayingLogic> logger,
    NowPlayingState state,
    IMemoryCache cache)
{
    private static readonly HashSet<string> SupportedOutputFormats =
        new(StringComparer.Ordinal) { "mp3", "flac", "opus256" };

    public EventCallback OnStateHasChanged { get; set; }
    public EventCallback<Track> CurrentTrackOnChanged { get; set; }
    public EventCallback JoinRequested { get; set; }
    public EventCallback PlayRequested { get; set; }
    public EventCallback StopRequested { get; set; }
    public EventCallback<double> VolumeOnChanged { get; set; }
    public EventCallback<(int Left, int Right)> ChannelMapOnChanged { get; set; }
    public EventCallback<int> LatencyAdjustRequested { get; set; }
    public EventCallback<string> StreamPairOnChanged { get; set; }

    /// <summary>
    /// Fired when a Plex track needs its split files prepared. The View handles this by
    /// calling /plex/prepare via JS fetch (which carries the browser cookie); the response
    /// flows back into <see cref="OnPlexSplitReadyAsync"/>.
    /// </summary>
    public EventCallback<Track> PlexPrepareRequested { get; set; }

    /// <summary>
    /// Fired when the user types in the Plex search box. The View handles this by calling
    /// /plex/search via JS fetch; results flow back into <see cref="OnPlexSearchResultsAsync"/>.
    /// </summary>
    public EventCallback<string> PlexSearchRequested { get; set; }

    /// <summary>
    /// Fired when the user picks an album from the Plex search results. The View handles
    /// this by fetching /plex/album/{ratingKey}/tracks and pushing the result back via
    /// <see cref="OnPlexAlbumTracksAsync"/>.
    /// </summary>
    public EventCallback<string> PlexAlbumPlayRequested { get; set; }

    /// <summary>
    /// Fired after a local album pick so the View can broadcast the selection to the
    /// group via SignalR. Not fired when mirroring a remote selection.
    /// </summary>
    public EventCallback<string> AlbumSelectionBroadcastRequested { get; set; }

    /// <summary>
    /// Fired after a local track pick (Next/Previous/album-list click) so the View
    /// can broadcast the selection to the group via SignalR. Not fired when mirroring
    /// a remote selection or when an album selection already handles the broadcast.
    /// </summary>
    public EventCallback<string> TrackSelectionBroadcastRequested { get; set; }

    /// <summary>
    /// Fired when the user picks a new output codec ("mp3"/"flac") so the View can
    /// broadcast the choice to the group via SignalR. Peers re-split with the new
    /// codec; the originator follows up with a fresh PlayRequested.
    /// </summary>
    public EventCallback<string> FormatSelectionBroadcastRequested { get; set; }

    private bool _mirroringRemote;
    private double? _pendingPlayAtMs;
    private string? _deferredRemoteTrackId;
    private bool _trackEndedNaturally;

    public async Task InitializeAsync()
    {
        try
        {
            logger.LogInformation("NowPlayingLogic.InitializeAsync: calling library.GetAllAsync");
            var all = await library.GetAllAsync();
            logger.LogInformation("NowPlayingLogic.InitializeAsync: library.GetAllAsync returned {Count} tracks", all.Count);
            state.Library = all;

            if (all.Count == 0)
            {
                logger.LogWarning("Track library is empty; nothing to play.");
                state.CurrentTrack = null;
                state.UpdateCoverUrl(null);
                state.Queue = [];
            }
            else
            {
                state.CurrentTrack = all[0];
                state.UpdateCoverUrl(all[0].CoverUrl);
                state.Queue = all.Skip(1).ToList();
            }

            state.Volume = state.Configuration.DefaultVolume;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to initialize NowPlaying library");
        }

        // Fire-and-forget split for the initial track. The View shows
        // IsSplitting state and waits to call setSrc until URLs are populated.
        if (state.CurrentTrack is { } first)
        {
            _ = SplitForCurrentTrackAsync(first);
        }
    }

    public async Task RestoreLocalTrackAsync(string trackId)
    {
        var idx = state.Library.ToList().FindIndex(t => t.Id == trackId);
        if (idx < 0) return;

        var track = state.Library[idx];
        state.Queue = state.Library.Skip(idx + 1).ToList();
        await ChangeTrackAsync(track);
    }

    /// <summary>
    /// Runs the per-track ffmpeg split (or hits cache) and populates
    /// <see cref="NowPlayingState.FrontStreamUrl"/> and <see cref="NowPlayingState.BackStreamUrl"/>.
    /// Called from <see cref="InitializeAsync"/> and <see cref="ChangeTrackAsync"/>.
    /// </summary>
    private async Task SplitForCurrentTrackAsync(Track track)
    {
        logger.LogInformation("SplitForCurrentTrackAsync: machineState={State}, track={TrackId}",
            state.Machine.State, track.Id);
        try
        {
            state.Machine.Fire(LocalDeviceTrigger.TrackSelected);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "SplitForCurrentTrackAsync: Machine.Fire(TrackSelected) failed in state {State}; split aborted for {TrackId}",
                state.Machine.State, track.Id);
            return;
        }
        state.FrontStreamUrl = null;
        state.BackStreamUrl = null;
        await OnStateHasChanged.InvokeAsync();

        if (track.Source == TrackSource.Plex)
        {
            // The Plex token is HttpOnly and only readable in HTTP endpoints. Delegate the
            // split to /plex/prepare via the browser; the View will call OnPlexSplitReadyAsync
            // once it has the URLs.
            await PlexPrepareRequested.InvokeAsync(track);
            return;
        }

        var failed = false;
        try
        {
            var result = await splitter.EnsureSplitAsync(
                track,
                state.ChannelMappingOverride,
                outputFormatOverride: state.OutputFormat);
            // If the user has switched tracks again while we were running, drop our result.
            if (state.CurrentTrack?.Id != track.Id) return;

            state.FrontStreamUrl = result.FrontUrl;
            state.BackStreamUrl = result.BackUrl;
            state.SourceChannelCount = result.SourceChannelCount;
            state.SourceChannelLayout = result.SourceChannelLayout;
            state.EffectiveChannelMapping = result.EffectiveMapping;

            // Notify the View — it will call setSrc with the URL matching the
            // currently selected stream pair.
            await CurrentTrackOnChanged.InvokeAsync(track);
        }
        catch (Exception ex)
        {
            failed = true;
            logger.LogError(ex, "Track split failed for {TrackId}", track.Id);
        }
        finally
        {
            if (state.Machine.State == LocalDeviceState.Splitting)
                state.Machine.Fire(failed ? LocalDeviceTrigger.SplitFailed : LocalDeviceTrigger.SplitDone);
            await OnStateHasChanged.InvokeAsync();
        }
    }

    public async Task RequestJoinAsync()
    {
        if (state.IsJoined) return;
        if (string.IsNullOrWhiteSpace(state.GroupName)) return;

        try
        {
            await JoinRequested.InvokeAsync();
            state.Machine.Fire(LocalDeviceTrigger.Joined);
            state.ActiveConnectionToast = new NowPlayingState.ConnectionToast(false, $"Connected to {state.GroupName}");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to join group {Group}", state.GroupName);
            var msg = ex.Message.Length > 120 ? ex.Message[..120] + "…" : ex.Message;
            state.ActiveConnectionToast = new NowPlayingState.ConnectionToast(true, msg);
        }
    }

    public async Task TogglePlayAsync()
    {
        if (state.CurrentTrack is null)
        {
            logger.LogDebug("TogglePlay ignored: no current track");
            return;
        }

        if (!state.IsJoined)
        {
            logger.LogDebug("TogglePlay ignored: not joined to a group");
            return;
        }

        try
        {
            if (state.IsPlaying)
                await StopRequested.InvokeAsync();
            else
                await PlayRequested.InvokeAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to toggle playback");
        }
    }

    public async Task StopPlayAsync()
    {
        if (state.CurrentTrack is null) return;
        if (!state.IsJoined) return;

        _trackEndedNaturally = false;
        try
        {
            await StopRequested.InvokeAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to stop playback");
        }
    }

    public async Task NextAsync()
    {
        if (state.Queue.Count == 0) return;

        var current = state.CurrentTrack;
        if (current is not null)
        {
            state.HistoryIds.Add(current.Id);
        }

        var nextTrack = state.Queue[0];
        state.Queue = state.Queue.Skip(1).ToList();
        await ChangeTrackAsync(nextTrack, broadcastToGroup: state.IsJoined);
    }

    public async Task PreviousAsync()
    {
        if (state.HistoryIds.Count == 0) return;

        var prevId = state.HistoryIds[^1];
        state.HistoryIds.RemoveAt(state.HistoryIds.Count - 1);

        var prevTrack = state.Library.FirstOrDefault(t => t.Id == prevId);
        if (prevTrack is null) return;

        var current = state.CurrentTrack;
        if (current is not null)
        {
            state.Queue = new[] { current }.Concat(state.Queue).ToList();
        }

        await ChangeTrackAsync(prevTrack, broadcastToGroup: state.IsJoined);
    }

    public async Task SelectFromQueueAsync(Track track)
    {
        if (state.CurrentTrack?.Id == track.Id) return;

        if (state.AlbumTracks.Count > 0)
        {
            var albumIdx = state.AlbumTracks.ToList().FindIndex(t => t.Id == track.Id);
            if (albumIdx < 0) return;

            state.HistoryIds.Clear();
            for (var i = 0; i < albumIdx; i++)
                state.HistoryIds.Add(state.AlbumTracks[i].Id);

            state.Queue = state.AlbumTracks.Skip(albumIdx + 1).ToList();
        }
        else
        {
            var idx = state.Queue.ToList().FindIndex(t => t.Id == track.Id);
            if (idx < 0) return;

            var current = state.CurrentTrack;
            if (current is not null)
                state.HistoryIds.Add(current.Id);

            var newQueue = state.Queue.ToList();
            newQueue.RemoveAt(idx);
            state.Queue = newQueue;
        }

        await ChangeTrackAsync(track, broadcastToGroup: state.IsJoined);
    }

    private async Task ChangeTrackAsync(Track track, bool broadcastToGroup = false)
    {
        var wasPlaying = state.IsPlaying || _trackEndedNaturally;
        _trackEndedNaturally = false;

        state.CurrentTrack = track;
        state.UpdateCoverUrl(track.CoverUrl);
        state.PositionSeconds = 0;
        state.DurationSeconds = 0;
        state.BufferedProgress = 0;

        try
        {
            // Run the split — this invokes CurrentTrackOnChanged once URLs are ready.
            await SplitForCurrentTrackAsync(track);

            if (broadcastToGroup)
                await TrackSelectionBroadcastRequested.InvokeAsync(track.Id);

            if (wasPlaying && state.IsJoined)
            {
                await PlayRequested.InvokeAsync();
            }

            await OnStateHasChanged.InvokeAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to change track to {TrackId}", track.Id);
        }
    }

    public Task ToggleQueueAsync()
    {
        state.QueueOpen = !state.QueueOpen;
        if (state.QueueOpen) state.PlexPanelOpen = false;
        return Task.CompletedTask;
    }

    public Task TogglePlexPanelAsync()
    {
        state.PlexPanelOpen = !state.PlexPanelOpen;
        if (state.PlexPanelOpen) state.QueueOpen = false;
        return Task.CompletedTask;
    }

    public Task ToggleDiscoverViewAsync()
    {
        state.DiscoverViewOpen = !state.DiscoverViewOpen;
        return Task.CompletedTask;
    }

    public Task ToggleChannelMapAsync()
    {
        state.ChannelMapOpen = !state.ChannelMapOpen;
        return Task.CompletedTask;
    }

    public Task ToggleFormatPanelAsync()
    {
        state.FormatOpen = !state.FormatOpen;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Switch the transcoding codec for the current group. When called with a different
    /// format than <see cref="NowPlayingState.OutputFormat"/>:
    /// <list type="number">
    ///   <item>updates state and notifies peers in the same group;</item>
    ///   <item>re-runs the per-track split with the new codec (cache-keyed by extension);</item>
    ///   <item>if currently playing, fires <see cref="PlayRequested"/> so the orchestrator
    ///   resets every member to Buffering and emits a fresh ScheduledPlay.</item>
    /// </list>
    /// Idle case: just saves the preference; the next PLAY uses it.
    /// </summary>
    public async Task SetOutputFormatAsync(string format)
    {
        if (!SupportedOutputFormats.Contains(format))
        {
            logger.LogDebug("SetOutputFormat ignored: unknown format '{Format}'", format);
            return;
        }
        if (string.Equals(state.OutputFormat, format, StringComparison.Ordinal)) return;

        var wasPlaying = state.IsPlaying;
        state.OutputFormat = format;

        try
        {
            if (state.IsJoined)
                await FormatSelectionBroadcastRequested.InvokeAsync(format);

            if (state.CurrentTrack is { } track)
            {
                await SplitForCurrentTrackAsync(track);

                if (wasPlaying && state.IsJoined)
                    await PlayRequested.InvokeAsync();
            }

            await OnStateHasChanged.InvokeAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to switch output format to {Format}", format);
        }
    }

    /// <summary>
    /// Called when another device in the group switches the output codec. Re-splits the
    /// current track with the new format so this peer is ready when the orchestrator's next
    /// OnRequestPlay resets it to Buffering. Does NOT fire PlayRequested — only the
    /// originator drives the restart.
    /// </summary>
    public async Task MirrorRemoteFormatChangeAsync(string format)
    {
        if (!SupportedOutputFormats.Contains(format))
        {
            logger.LogDebug("MirrorRemoteFormatChange ignored: unknown format '{Format}'", format);
            return;
        }
        if (string.Equals(state.OutputFormat, format, StringComparison.Ordinal)) return;

        state.OutputFormat = format;

        if (state.CurrentTrack is { } track)
            await SplitForCurrentTrackAsync(track);

        await OnStateHasChanged.InvokeAsync();
    }

    public async Task SetVolumeAsync(double value)
    {
        var clamped = Math.Clamp(value, 0.0, 1.0);
        if (Math.Abs(state.Volume - clamped) < 0.0005) return;

        state.Volume = clamped;

        try
        {
            await VolumeOnChanged.InvokeAsync(clamped);
            await OnStateHasChanged.InvokeAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to apply volume {Volume}", clamped);
        }
    }

    public async Task SetChannelMapAsync(int left, int right)
    {
        var ch = state.Channels > 0 ? state.Channels : 2;
        var l = Math.Clamp(left, 0, ch - 1);
        var r = Math.Clamp(right, 0, ch - 1);

        state.LeftChannel = l;
        state.RightChannel = r;

        try
        {
            await ChannelMapOnChanged.InvokeAsync((l, r));
            await OnStateHasChanged.InvokeAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to apply channel map [{L},{R}]", l, r);
        }
    }

    public void OnPositionTick(double position, double duration)
    {
        state.PositionSeconds = position;
        if (duration > 0) state.DurationSeconds = duration;
    }

    public void OnBufferedTick(double buffered) => state.BufferedProgress = buffered;

    public async Task OnPlayStateChangedAsync(bool isPlaying)
    {
        if (state.IsPlaying == isPlaying) return;
        // Machine clears IsWaitingForPeers via PlaybackBegan event on entry to Playing.
        var trigger = isPlaying ? LocalDeviceTrigger.PlayStarted : LocalDeviceTrigger.PlaybackEnded;
        if (state.Machine.CanFire(trigger)) state.Machine.Fire(trigger);
        await OnStateHasChanged.InvokeAsync();
    }

    public async Task OnTrackEndedNaturally()
    {
        _trackEndedNaturally = true;
        await NextAsync();
    }

    public async Task OnWaitingForPeersAsync()
    {
        if (state.IsWaitingForPeers) return;
        state.IsWaitingForPeers = true;
        await OnStateHasChanged.InvokeAsync();
    }

    public async Task OnReadyChangedAsync(bool isReady)
    {
        if (state.IsReady == isReady) return;
        // isReady=true → BufferReady (Buffering→Ready, or Playing reentry); isReady=false → no-op
        // (the JS only reports false on track change, which is handled by the TrackSelected trigger
        // in SplitForCurrentTrackAsync).
        if (isReady && state.Machine.CanFire(LocalDeviceTrigger.BufferReady))
            state.Machine.Fire(LocalDeviceTrigger.BufferReady);
        await OnStateHasChanged.InvokeAsync();
    }

    public async Task OnDecodeErrorAsync(string message)
    {
        if (state.Machine.State != LocalDeviceState.Buffering) return;
        state.Machine.Fire(LocalDeviceTrigger.SplitFailed);
        state.ActiveConnectionToast = new NowPlayingState.ConnectionToast(true,
            $"Audio decode failed: {message}");
        await OnStateHasChanged.InvokeAsync();
    }

    public async Task OnLatencyOffsetChangedAsync(int offsetMs)
    {
        if (state.LatencyOffsetMs == offsetMs) return;
        state.LatencyOffsetMs = offsetMs;
        await OnStateHasChanged.InvokeAsync();
    }

    /// <summary>
    /// Apply a manual channel-index mapping for the current track. Re-runs the
    /// split with the new indices (cached on disk per-mapping) and reloads the
    /// audio element. Session-only — never persisted.
    /// </summary>
    public async Task SetChannelMappingAsync(ChannelMapping mapping)
    {
        if (state.CurrentTrack is null) return;
        if (state.ChannelMappingOverride == mapping) return;

        state.ChannelMappingOverride = mapping;
        await SplitForCurrentTrackAsync(state.CurrentTrack);
        // SplitForCurrentTrackAsync's CurrentTrackOnChanged callback already
        // tells the View to setSrc with the URL of the currently-selected pair.
    }

    /// <summary>Clear any user override and fall back to the splitter's auto-detection.</summary>
    public async Task ResetChannelMappingAsync()
    {
        if (state.CurrentTrack is null) return;
        if (state.ChannelMappingOverride is null) return;

        state.ChannelMappingOverride = null;
        await SplitForCurrentTrackAsync(state.CurrentTrack);
    }

    /// <summary>
    /// Pick which pre-split stereo stream this device plays. Each device picks
    /// once (Front or Back), giving you a two-device quadro setup.
    /// </summary>
    public async Task SelectStreamPairAsync(string pair)
    {
        if (state.SelectedStreamPair == pair) return;
        state.SelectedStreamPair = pair;

        try
        {
            await StreamPairOnChanged.InvokeAsync(pair);
            await OnStateHasChanged.InvokeAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to switch stream pair to {Pair}", pair);
        }
    }

    public async Task AdjustLatencyAsync(int deltaMs)
    {
        try
        {
            await LatencyAdjustRequested.InvokeAsync(deltaMs);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to adjust latency by {Delta}ms", deltaMs);
        }
    }

    public void ApplyAudioInfo(int channels, int left, int right)
    {
        state.Channels = channels;
        state.LeftChannel = left;
        state.RightChannel = right;
    }

    public void ApplyOffset(long offsetMs) => state.OffsetMs = offsetMs;

    public void ApplyMemberCount(int count) => state.MemberCount = count;

    public void SetParallax(double x, double y)
    {
        state.ParallaxX = Math.Clamp(x, -0.5, 0.5);
        state.ParallaxY = Math.Clamp(y, -0.5, 0.5);
    }

    // ─── PLEX ─────────────────────────────────────────────────────────

    /// <summary>
    /// The product is scoped to multichannel DTS albums only — this is the fixed search
    /// query used against Plex's section-/all endpoint. Anything else hits the local library.
    /// </summary>
    public const string PlexFixedQuery = "DTS";

    private static string DtsCacheKey(string? username) => $"plex-dts:{username}";
    private static readonly TimeSpan DtsCacheTtl = TimeSpan.FromMinutes(10);

    /// <summary>How many albums to load per page. Tuned to roughly fill one screenful.</summary>
    public const int PlexPageSize = 50;

    /// <summary>
    /// Called from the View on initial render (when HttpContext is available) and on
    /// connect/disconnect callbacks from <see cref="PlexConnectPanel"/>. When connecting,
    /// auto-triggers the DTS album search so results appear without user input.
    /// </summary>
    public async Task SetPlexConnectionStateAsync(bool connected, string? username)
    {
        var changed = state.IsPlexConnected != connected || state.PlexUsername != username;
        state.IsPlexConnected = connected;
        state.PlexUsername = username;
        if (!connected)
        {
            state.PlexAlbumResults = [];
            state.PlexSearchQuery = string.Empty;
            state.PlexAlbumTotal = 0;
            state.IsPlexSearching = false;
            state.IsPlexLoadingMore = false;
        }
        if (changed) await OnStateHasChanged.InvokeAsync();

        if (connected)
        {
            await LoadDtsAlbumsAsync();
        }
    }

    /// <summary>
    /// Fires the FIRST page of the fixed-query DTS album search. Clears any previous
    /// results. The View handles the actual HTTP fetch and pushes results back via
    /// <see cref="OnPlexAlbumResultsAsync"/>.
    /// </summary>
    public async Task LoadDtsAlbumsAsync()
    {
        state.PlexSearchQuery = PlexFixedQuery;

        if (cache.TryGetValue(DtsCacheKey(state.PlexUsername),
                out (IReadOnlyList<PlexAlbumSummary> albums, int total) hit))
        {
            state.PlexAlbumResults = hit.albums;
            state.PlexAlbumTotal   = hit.total;
            state.IsPlexSearching  = false;
            await OnStateHasChanged.InvokeAsync();
            return;
        }

        state.PlexAlbumResults = [];
        state.PlexAlbumTotal = 0;
        state.IsPlexSearching = true;
        await OnStateHasChanged.InvokeAsync();
        // offset 0 = first page. The View's handler reads state.PlexAlbumResults.Count
        // when it builds the request, but we explicitly reset to [] above so the offset
        // is unambiguously zero.
        await PlexSearchRequested.InvokeAsync(PlexFixedQuery);
    }

    public async Task RefreshDtsAlbumsAsync()
    {
        cache.Remove(DtsCacheKey(state.PlexUsername));
        await LoadDtsAlbumsAsync();
    }

    public async Task SearchPlexAlbumsAsync(string query)
    {
        query = string.IsNullOrWhiteSpace(query) ? PlexFixedQuery : query.Trim();
        state.PlexSearchQuery = query;
        state.PlexAlbumResults = [];
        state.PlexAlbumTotal = 0;
        state.IsPlexSearching = true;
        await OnStateHasChanged.InvokeAsync();
        await PlexSearchRequested.InvokeAsync(query);
    }

    /// <summary>
    /// Fires another page of results — invoked by the View when the bottom-of-list
    /// IntersectionObserver fires. Idempotent if a fetch is already in flight or there
    /// are no more pages.
    /// </summary>
    public async Task LoadMoreDtsAlbumsAsync()
    {
        if (!state.IsPlexConnected) return;
        if (state.IsPlexSearching || state.IsPlexLoadingMore) return;
        if (!state.HasMorePlexAlbums) return;

        state.IsPlexLoadingMore = true;
        await OnStateHasChanged.InvokeAsync();
        await PlexSearchRequested.InvokeAsync(state.PlexSearchQuery);
    }

    /// <summary>
    /// Called by the View when /plex/search returns. The page boundary is taken from the
    /// number of items already in <see cref="NowPlayingState.PlexAlbumResults"/> at the
    /// time the request was sent — see <see cref="GetNextPlexOffset"/>. The handler
    /// appends results and updates the absolute total.
    /// </summary>
    public async Task OnPlexAlbumResultsAsync(string forQuery, IReadOnlyList<PlexAlbumSummary> page, int total)
    {
        if (!string.Equals(state.PlexSearchQuery, forQuery, StringComparison.Ordinal)) return;

        if (state.PlexAlbumResults.Count == 0)
        {
            state.PlexAlbumResults = page;
        }
        else
        {
            // De-dupe against existing rating keys in case a paged request raced with a reset.
            var seen = state.PlexAlbumResults.Select(a => a.RatingKey).ToHashSet(StringComparer.Ordinal);
            var merged = new List<PlexAlbumSummary>(state.PlexAlbumResults);
            foreach (var a in page)
            {
                if (seen.Add(a.RatingKey)) merged.Add(a);
            }
            state.PlexAlbumResults = merged;
        }
        state.PlexAlbumTotal = total;
        state.IsPlexSearching = false;
        state.IsPlexLoadingMore = false;
        if (string.Equals(forQuery, PlexFixedQuery, StringComparison.Ordinal))
            cache.Set(DtsCacheKey(state.PlexUsername), (state.PlexAlbumResults, state.PlexAlbumTotal), DtsCacheTtl);
        await OnStateHasChanged.InvokeAsync();
    }

    public async Task OnPlexSearchFailedAsync(string forQuery)
    {
        if (!string.Equals(state.PlexSearchQuery, forQuery, StringComparison.Ordinal)) return;
        // Don't wipe accumulated results on a transient page failure; just stop the spinner.
        state.IsPlexSearching = false;
        state.IsPlexLoadingMore = false;
        await OnStateHasChanged.InvokeAsync();
    }

    /// <summary>
    /// The next offset to request — equals the number of albums currently held in state.
    /// The View reads this immediately before issuing the fetch.
    /// </summary>
    public int GetNextPlexOffset() => state.PlexAlbumResults.Count;

    /// <summary>
    /// User picked an album. Fire the request to the View; it will fetch the track
    /// listing via /plex/album/{rk}/tracks and call <see cref="OnPlexAlbumTracksAsync"/>.
    /// </summary>
    public async Task PlayPlexAlbumAsync(string ratingKey)
    {
        if (string.IsNullOrEmpty(ratingKey)) return;
        state.IsPlexLoadingAlbum = true;
        state.PlexPanelOpen = false;
        await OnStateHasChanged.InvokeAsync();
        await PlexAlbumPlayRequested.InvokeAsync(ratingKey);

        if (state.IsJoined && !_mirroringRemote)
            await AlbumSelectionBroadcastRequested.InvokeAsync(ratingKey);
    }

    /// <summary>
    /// Called when another device in the group picks a Plex album. Reuses the standard
    /// album-load pipeline but suppresses the outgoing broadcast to prevent an echo.
    /// Silently skips if this device has no Plex session.
    /// </summary>
    public async Task MirrorRemoteAlbumSelectionAsync(string albumRatingKey)
    {
        if (!state.IsPlexConnected) return;
        if (string.IsNullOrEmpty(albumRatingKey)) return;
        if (string.Equals(state.PlexCurrentAlbumRatingKey, albumRatingKey, StringComparison.Ordinal)) return;

        _mirroringRemote = true;
        try   { await PlayPlexAlbumAsync(albumRatingKey); }
        finally { _mirroringRemote = false; }
    }

    /// <summary>
    /// Called when another device in the group selects a track. Starts the local
    /// transcode → buffer pipeline without triggering auto-play; ScheduledPlay
    /// coordinates the actual playback moment.
    /// </summary>
    public async Task MirrorRemoteTrackSelectionAsync(string trackId)
    {
        if (string.IsNullOrEmpty(trackId)) return;
        _trackEndedNaturally = false;

        var track = state.Library.FirstOrDefault(t => t.Id == trackId)
                    ?? state.Queue.FirstOrDefault(t => t.Id == trackId)
                    ?? state.AlbumTracks.FirstOrDefault(t => t.Id == trackId);
        if (track is null)
        {
            if (state.IsPlexLoadingAlbum)
            {
                _deferredRemoteTrackId = trackId;
                return;
            }
            logger.LogWarning("Remote track selection for unknown track {TrackId}", trackId);
            return;
        }

        if (state.CurrentTrack?.Id == trackId) return;

        var current = state.CurrentTrack;
        if (current is not null)
            state.HistoryIds.Add(current.Id);

        var idx = state.Queue.ToList().FindIndex(t => t.Id == trackId);
        if (idx >= 0)
        {
            var q = state.Queue.ToList();
            q.RemoveAt(idx);
            state.Queue = q;
        }

        state.CurrentTrack = track;
        state.UpdateCoverUrl(track.CoverUrl);
        state.PositionSeconds = 0;
        state.DurationSeconds = 0;
        state.BufferedProgress = 0;

        await OnStateHasChanged.InvokeAsync();
        await SplitForCurrentTrackAsync(track);
    }

    public double? ConsumePendingPlayAtMs()
    {
        var v = _pendingPlayAtMs;
        _pendingPlayAtMs = null;
        return v;
    }

    public async Task SwitchTrackForRemotePlayAsync(Track track, double pendingPlayAtMs)
    {
        _pendingPlayAtMs = pendingPlayAtMs;
        _trackEndedNaturally = false;

        state.CurrentTrack = track;
        state.UpdateCoverUrl(track.CoverUrl);
        state.PositionSeconds = 0;
        state.DurationSeconds = 0;
        state.BufferedProgress = 0;

        await OnStateHasChanged.InvokeAsync();
        await SplitForCurrentTrackAsync(track);
    }

    /// <summary>
    /// Called by the View once the album's tracks have been fetched. Replaces the queue
    /// with the album, sets the first track as Now Playing, and kicks off the split.
    /// </summary>
    public async Task OnPlexAlbumTracksAsync(string ratingKey, IReadOnlyList<Track> tracks)
    {
        state.IsPlexLoadingAlbum = false;
        state.PlexCurrentAlbumRatingKey = ratingKey;
        if (tracks.Count == 0)
        {
            logger.LogInformation("Plex album {RatingKey} returned no tracks", ratingKey);
            await OnStateHasChanged.InvokeAsync();
            return;
        }

        var current = state.CurrentTrack;
        if (current is not null)
        {
            state.HistoryIds.Add(current.Id);
        }

        state.AlbumTracks = tracks;
        state.HistoryIds.Clear();

        var first = tracks[0];
        state.Queue = tracks.Skip(1).ToList();
        await ChangeTrackAsync(first);

        if (_deferredRemoteTrackId is { } deferred)
        {
            _deferredRemoteTrackId = null;
            await MirrorRemoteTrackSelectionAsync(deferred);
        }
    }

    public async Task OnPlexAlbumTracksFailedAsync(string ratingKey, string reason)
    {
        logger.LogWarning("Failed to load Plex album {RatingKey}: {Reason}", ratingKey, reason);
        state.IsPlexLoadingAlbum = false;
        await OnStateHasChanged.InvokeAsync();
    }

    /// <summary>
    /// Called by the View when /plex/prepare returns front/back URLs for a Plex track.
    /// Mirrors the local-split completion path so playback can begin.
    /// </summary>
    public async Task OnPlexSplitReadyAsync(
        string trackId,
        string frontUrl,
        string backUrl,
        int? channelCount = null,
        string? channelLayout = null,
        ChannelMapping? effectiveMapping = null)
    {
        if (state.CurrentTrack?.Id != trackId)
        {
            // user switched tracks while prepare was running
            return;
        }
        state.FrontStreamUrl = frontUrl;
        state.BackStreamUrl = backUrl;
        if (channelCount is { } c) state.SourceChannelCount = c;
        if (channelLayout is not null) state.SourceChannelLayout = channelLayout;
        if (effectiveMapping is not null) state.EffectiveChannelMapping = effectiveMapping;
        if (state.Machine.State == LocalDeviceState.Splitting)
            state.Machine.Fire(LocalDeviceTrigger.SplitDone);
        await CurrentTrackOnChanged.InvokeAsync(state.CurrentTrack);
        await OnStateHasChanged.InvokeAsync();
    }

    public async Task OnPlexSplitFailedAsync(string trackId, string reason)
    {
        if (state.CurrentTrack?.Id != trackId) return;
        logger.LogWarning("Plex prepare failed for {TrackId}: {Reason}", trackId, reason);
        if (state.Machine.State == LocalDeviceState.Splitting)
            state.Machine.Fire(LocalDeviceTrigger.SplitFailed);
        await OnStateHasChanged.InvokeAsync();
    }
}
