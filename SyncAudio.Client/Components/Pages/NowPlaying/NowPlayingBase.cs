using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using SyncAudio.Core.Models;
using SyncAudio.Client.Components.Pages.NowPlaying.Panels;
using SyncAudio.Core.Services.Plex;
using SyncAudio.Core.Services.TrackSplit;

namespace SyncAudio.Client.Components.Pages.NowPlaying;

public abstract class NowPlayingBase : ComponentBase, IAsyncDisposable
{
    [Inject] protected IJSRuntime JS { get; set; } = null!;
    [Inject] protected INowPlayingContextFactory ContextFactory { get; set; } = null!;
    [Inject] protected IPlexSessionStore PlexSessionStore { get; set; } = null!;
    [Inject] protected ILogger<NowPlayingBase> Logger { get; set; } = null!;
    [Inject] protected NavigationManager Navigation { get; set; } = null!;

    [Parameter] public string? RoomId { get; set; }

    protected NowPlayingContext _context = null!;
    public const string DiscoverLoadMoreSentinelId = "discover-load-more-sentinel";

    private DotNetObjectReference<NowPlayingBase>? _selfRef;
    private bool _disposed;
    private bool _jsInitialized;
    private bool _autoLoadDtsOnFirstRender;
    private bool _plexObserverInstalled;
    private bool _discoverObserverInstalled;
    private bool _pendingAutoJoin;
    private bool _pendingRoomIdRestore;
    private bool _prevIsSplitting;
    private bool _prevIsJoined;
    protected bool _syncOpen;
    protected bool _formatOpen;
    private CancellationTokenSource? _toastDismissCts;

    // Subclasses override this to redirect to their own base route when no RoomId is provided.
    protected virtual string RoomIdRedirectRoute(string roomId) => $"/{roomId}";

    // Returns a redirect URL when this component is opened on the wrong screen size, or null
    // when no redirect is needed. Subclasses override to enforce their layout variant.
    protected virtual string? RedirectUrlForViewport(int viewportWidth) => null;

    protected override Task OnInitializedAsync()
    {
        _context = ContextFactory.Create();
        _context.State.OnStateChanged += OnStateChangedAsync;

        _context.Logic.OnStateHasChanged                 = EventCallback.Factory.Create(this, OnStateChangedAsync);
        _context.Logic.CurrentTrackOnChanged             = EventCallback.Factory.Create<Track>(this, HandleCurrentTrackChanged);
        _context.Logic.JoinRequested                     = EventCallback.Factory.Create(this, HandleJoinInterop);
        _context.Logic.PlayRequested                     = EventCallback.Factory.Create(this, HandlePlayInterop);
        _context.Logic.StopRequested                     = EventCallback.Factory.Create(this, HandleStopInterop);
        _context.Logic.VolumeOnChanged                   = EventCallback.Factory.Create<double>(this, HandleVolumeInterop);
        _context.Logic.ChannelMapOnChanged               = EventCallback.Factory.Create<(int Left, int Right)>(this, HandleChannelMapInterop);
        _context.Logic.LatencyAdjustRequested            = EventCallback.Factory.Create<int>(this, HandleLatencyAdjustInterop);
        _context.Logic.StreamPairOnChanged               = EventCallback.Factory.Create<string>(this, HandleStreamPairChanged);
        _context.Logic.PlexPrepareRequested              = EventCallback.Factory.Create<Track>(this, HandlePlexPrepareRequested);
        _context.Logic.PlexSearchRequested               = EventCallback.Factory.Create<string>(this, HandlePlexSearchRequested);
        _context.Logic.PlexAlbumPlayRequested            = EventCallback.Factory.Create<string>(this, HandlePlexAlbumPlayRequested);
        _context.Logic.AlbumSelectionBroadcastRequested  = EventCallback.Factory.Create<string>(this, HandleAlbumSelectionBroadcast);
        _context.Logic.TrackSelectionBroadcastRequested  = EventCallback.Factory.Create<string>(this, HandleTrackSelectionBroadcast);
        _context.Logic.FormatSelectionBroadcastRequested = EventCallback.Factory.Create<string>(this, HandleFormatSelectionBroadcast);

        // Seed connection state from the cookie. This is the only point in the
        // component lifecycle where IHttpContextAccessor.HttpContext is available
        // (it's null during all subsequent SignalR-driven events).
        var session = PlexSessionStore.Get();
        if (session is not null)
        {
            _context.State.IsPlexConnected = true;
            _context.State.PlexUsername = session.Username;
            _autoLoadDtsOnFirstRender = true;
        }

        if (string.IsNullOrWhiteSpace(RoomId))
        {
            // Defer room-ID resolution to OnAfterRenderAsync so we can query localStorage
            // first. This is necessary for iOS PWA Home Screen launches, which always open
            // at start_url "/" regardless of the URL used when "Add to Home Screen" was tapped.
            _pendingRoomIdRestore = true;
            return Task.CompletedTask;
        }

        _context.State.GroupName = RoomId;
        _pendingAutoJoin = true;

        return _context.Logic.InitializeAsync();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_disposed) return;

        // After every render, (re)install the IntersectionObserver against the sentinel
        // — Blazor may have torn down and re-rendered the wrapper, so the observer needs
        // to be re-bound to the live DOM node.
        if (_jsInitialized && _selfRef is not null)
        {
            var hasSentinel = _context.State is { IsPlexConnected: true, HasMorePlexAlbums: true, PlexPanelOpen: true };
            try
            {
                if (hasSentinel && !_plexObserverInstalled)
                {
                    var ok = await JS.InvokeAsync<bool>("plexInterop.installLoadMoreObserver",
                        PlexSheet.LoadMoreSentinelId, _selfRef, nameof(LoadMorePlexAlbums));
                    _plexObserverInstalled = ok;
                }
                else if (!hasSentinel && _plexObserverInstalled)
                {
                    await JS.InvokeVoidAsync("plexInterop.uninstallLoadMoreObserver", PlexSheet.LoadMoreSentinelId);
                    _plexObserverInstalled = false;
                }
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Plex observer (un)install failed");
            }

            var hasDiscoverSentinel = _context.State is { DiscoverViewOpen: true, HasMorePlexAlbums: true };
            try
            {
                if (hasDiscoverSentinel && !_discoverObserverInstalled)
                {
                    await JS.InvokeVoidAsync("discoverInterop.installDiscoverLoadMore",
                        DiscoverLoadMoreSentinelId, _selfRef, nameof(LoadMorePlexAlbums));
                    _discoverObserverInstalled = true;
                }
                else if (!hasDiscoverSentinel && _discoverObserverInstalled)
                {
                    await JS.InvokeVoidAsync("discoverInterop.uninstallDiscoverLoadMore", DiscoverLoadMoreSentinelId);
                    _discoverObserverInstalled = false;
                }
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Discover observer (un)install failed");
            }
        }

        if (!firstRender) return;

        if (_pendingRoomIdRestore)
        {
            _pendingRoomIdRestore = false;
            string? savedRoomId = null;
            try { savedRoomId = await JS.InvokeAsync<string?>("syncAudio.loadRoomId"); } catch { }
            var roomId = !string.IsNullOrWhiteSpace(savedRoomId)
                ? savedRoomId
                : NowPlayingHelpers.GenerateRoomId();
            Navigation.NavigateTo(RoomIdRedirectRoute(roomId), replace: true);
            return;
        }

        if (!string.IsNullOrWhiteSpace(RoomId))
        {
            try
            {
                var width = await JS.InvokeAsync<int>("syncAudio.getViewportWidth");
                if (RedirectUrlForViewport(width) is { } redirectUrl)
                {
                    Navigation.NavigateTo(redirectUrl, replace: true);
                    return;
                }
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Viewport width check failed — proceeding with current variant");
            }
        }

        try
        {
            _selfRef = DotNetObjectReference.Create(this);
            // Initialize the JS engine without an audio URL — the split runs
            // server-side and pushes the URL via HandleCurrentTrackChanged
            // once it completes (or pulls from cache, instantly).
            var initialUrl = _context.State.CurrentStreamUrl ?? string.Empty;
            Logger.LogInformation("NowPlayingBase: initializing JS engine, initialUrl={Url}", initialUrl);
            await JS.InvokeVoidAsync("syncAudio.initialize", _selfRef, initialUrl);
            await JS.InvokeVoidAsync("syncAudio.setVolume", _context.State.Volume);
            _jsInitialized = true;
            Logger.LogInformation("NowPlayingBase: JS engine initialized");
            try { await JS.InvokeVoidAsync("syncAudio.saveRoomId", RoomId!); } catch { }

            // Restore stream pair before track restore so CurrentStreamUrl picks the right pair.
            var savedPair = await JS.InvokeAsync<string?>("syncAudio.loadStreamPair");
            if (savedPair is "Front" or "Back")
                await _context.Logic.SelectStreamPairAsync(savedPair);

            // Restore last-selected track from localStorage (before join so group state wins).
            try
            {
                var saved = await JS.InvokeAsync<JsonElement?>("syncAudio.loadCurrentTrack");
                if (saved.HasValue)
                {
                    var trackId   = saved.Value.GetProperty("trackId").GetString();
                    var source    = saved.Value.GetProperty("source").GetString();
                    var ratingKey = saved.Value.TryGetProperty("ratingKey", out var rk)
                                    && rk.ValueKind == JsonValueKind.String ? rk.GetString() : null;

                    Logger.LogInformation("NowPlayingBase: restoring track from localStorage, trackId={TrackId} source={Source}", trackId, source);
                    if (trackId is not null)
                    {
                        if (source == nameof(TrackSource.Local))
                        {
                            await _context.Logic.RestoreLocalTrackAsync(trackId);
                        }
                        else if (source == nameof(TrackSource.Plex)
                                 && _context.State.IsPlexConnected
                                 && ratingKey is not null)
                        {
                            await HandlePlexAlbumPlayRequested(ratingKey);
                            // Reposition to the saved track without triggering a split.
                            var restoreTarget = _context.State.Queue.FirstOrDefault(t => t.Id == trackId);
                            if (restoreTarget is not null)
                                await _context.Logic.PositionToPlexTrackAsync(restoreTarget);
                            // else: saved track is already track[0] (CurrentTrack).
                        }
                    }
                    Logger.LogInformation("NowPlayingBase: track restore complete");
                }
                else
                {
                    Logger.LogInformation("NowPlayingBase: no saved track in localStorage");
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to restore last track from localStorage");
            }

            if (_pendingAutoJoin)
            {
                _pendingAutoJoin = false;
                Logger.LogInformation("NowPlayingBase: auto-joining group {Group}", _context.State.GroupName);
                await _context.Logic.RequestJoinAsync();
                Logger.LogInformation("NowPlayingBase: auto-join complete, machineState={State}", _context.State.Machine.State);
            }

            // If the split already finished while we were rendering (cache hit),
            // push the URL now so the audio element gets it before the user
            // clicks PLAY.
            if (!string.IsNullOrEmpty(_context.State.CurrentStreamUrl))
            {
                await JS.InvokeVoidAsync("syncAudio.setSrc", _context.State.CurrentStreamUrl);
            }

            if (_autoLoadDtsOnFirstRender)
            {
                _autoLoadDtsOnFirstRender = false;
                await _context.Logic.LoadDtsAlbumsAsync();
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to initialize JS engine");
        }
    }

    private async Task OnStateChangedAsync()
    {
        var s = _context.State;
        if (!_prevIsJoined && s.Machine.IsJoined)
        {
            _syncOpen = false;
        }
        _prevIsJoined = s.Machine.IsJoined;

        if (s.ActiveConnectionToast is { IsError: false })
        {
            await (_toastDismissCts?.CancelAsync() ?? Task.CompletedTask);
            
            _toastDismissCts = new CancellationTokenSource();
            var cts = _toastDismissCts;
            
            _ = Task.Delay(1000, cts.Token).ContinueWith(async t =>
            {
                if (t.IsCanceled) return;
                _context.State.ActiveConnectionToast = null;
                await InvokeAsync(StateHasChanged);
            }, TaskScheduler.Default);
        }

        if (_jsInitialized && s.Machine.IsSplitting != _prevIsSplitting)
        {
            _prevIsSplitting = s.Machine.IsSplitting;
            if (s.Machine.IsSplitting)
            {
                // Report new track to hub before splitting phase so PeersOverlay shows correct title.
                if (s.CurrentTrack is { } t)
                {
                    await JS.InvokeVoidAsync("syncAudio.reportNowPlaying", t.Id, t.Title, t.Artist, t.CoverUrl ?? "");
                }
                await JS.InvokeVoidAsync("syncAudio.setSplitting", true);
            }
            // When splitting ends, setSrc fires next and reports 'buffering' itself.
        }
        await InvokeAsync(StateHasChanged);
    }

    // ─── EventCallback handlers (View → JS) ─────────────────────────

    private async Task HandleJoinInterop()
    {
        if (!_jsInitialized) return;
        const int maxAttempts = 5;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await JS.InvokeVoidAsync("syncAudio.connectAndJoin", _context.State.GroupName);
                return;
            }
            catch (JSException) when (attempt < maxAttempts)
            {
                if (attempt == 2)
                {
                    _syncOpen = true;
                    await InvokeAsync(StateHasChanged);
                }
                await Task.Delay(500 * attempt);
            }
        }
    }

    private async Task HandlePlayInterop()
    {
        if (!_jsInitialized) return;
        var albumKey = _context.State.PlexCurrentAlbumRatingKey;
        if (_context.State.Machine.IsJoined && !string.IsNullOrEmpty(albumKey))
            await JS.InvokeVoidAsync("syncAudio.selectAlbum", albumKey);
        await JS.InvokeVoidAsync("syncAudio.requestPlay", _context.State.CurrentTrack?.Id ?? string.Empty);
    }

    private async Task HandleStopInterop()
    {
        if (!_jsInitialized) return;
        await JS.InvokeVoidAsync("syncAudio.requestStop");
    }

    private async Task HandleCurrentTrackChanged(Track track)
    {
        if (!_jsInitialized) return;

        await JS.InvokeVoidAsync("syncAudio.saveCurrentTrack",
            track.Id,
            track.Source.ToString(),
            _context.State.PlexCurrentAlbumRatingKey);

        // The Logic invokes this AFTER the split completes, so State has URLs.
        // Pick whichever pair the user has selected on this device.
        var url = _context.State.CurrentStreamUrl;
        if (string.IsNullOrEmpty(url))
        {
            Logger.LogWarning("CurrentTrackOnChanged fired but stream URL is null for {TrackId}", track.Id);
            return;
        }
        await JS.InvokeVoidAsync("syncAudio.setSrc", url);
        await JS.InvokeVoidAsync("syncAudio.reportNowPlaying",
            track.Id, track.Title, track.Artist, track.CoverUrl ?? "");
        await JS.InvokeVoidAsync("syncAudio.setNowPlayingMetadata",
            track.Title, track.Artist, track.Album, track.CoverUrl ?? "");
        await JS.InvokeVoidAsync("updateTouchIcon", track.Id);

        var pendingPlayAt = _context.Logic.ConsumePendingPlayAtMs();
        if (pendingPlayAt.HasValue)
        {
            await JS.InvokeVoidAsync("syncAudio.schedulePlay", pendingPlayAt.Value);
        }
    }

    private async Task HandleVolumeInterop(double v)
    {
        if (!_jsInitialized) return;
        await JS.InvokeVoidAsync("syncAudio.setVolume", v);
    }

    private async Task HandleChannelMapInterop((int Left, int Right) m)
    {
        if (!_jsInitialized) return;
        await JS.InvokeVoidAsync("syncAudio.setChannelMap", m.Left, m.Right);
    }

    private async Task HandleLatencyAdjustInterop(int deltaMs)
    {
        if (!_jsInitialized) return;
        await JS.InvokeVoidAsync("syncAudio.adjustLatency", deltaMs);
    }

    private async Task HandleStreamPairChanged(string pair)
    {
        if (!_jsInitialized) return;
        await JS.InvokeVoidAsync("syncAudio.saveStreamPair", pair);
        var url = pair == "Back" ? _context.State.BackStreamUrl : _context.State.FrontStreamUrl;
        if (string.IsNullOrEmpty(url))
        {
            // Split hasn't completed yet — HandleCurrentTrackChanged will fire setSrc when it does.
            return;
        }
        await JS.InvokeVoidAsync("syncAudio.setSrc", url);
    }

    // ─── User-event handlers (View → Logic) ─────────────────────────

    protected async Task HandleJoin(string groupName)
    {
        _context.State.GroupName = groupName;
        await _context.Logic.RequestJoinAsync();
        if (_jsInitialized)
            try { await JS.InvokeVoidAsync("syncAudio.saveRoomId", groupName); } catch { }
    }

    protected Task HandleAlbumRowClick(Track t)
    {
        var current = _context.State.CurrentTrack;
        if (current is not null && t.Id == current.Id)
        {
            return _context.Logic.TogglePlayAsync();
        }
        return _context.Logic.SelectFromQueueAsync(t, play: true);
    }

    protected async Task HandlePlexSheetTrackClick(Track t)
    {
        await _context.Logic.TogglePlexPanelAsync();
        await HandleAlbumRowClick(t);
    }

    // ─── Discover handlers ──────────────────────────────────────────

    protected async Task HandleDiscoverAlbumSelect(string ratingKey)
    {
        await _context.Logic.ToggleDiscoverViewAsync();
        await _context.Logic.PlayPlexAlbumAsync(ratingKey);

        // Let the pending render (PlexSheet expanding the row with its tracklist)
        // flush before we query the DOM for the row to scroll into view.
        await InvokeAsync(StateHasChanged);
        await Task.Yield();

        if (!_jsInitialized) return;
        try
        {
            await JS.InvokeVoidAsync("plexInterop.scrollAlbumIntoView", ratingKey);
        }
        catch (JSDisconnectedException) { }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "scrollAlbumIntoView failed for {RatingKey}", ratingKey);
        }
    }

    // ─── Plex handlers ──────────────────────────────────────────────

    /// <summary>
    /// Logic asked us to fetch a page of DTS albums. The offset is taken from the number
    /// of albums Logic is currently holding (so the FIRST call after a reset starts at 0,
    /// subsequent "load more" calls continue from the end). Calls /plex/search via the
    /// browser (so the cookie is carried) and pushes the page back into Logic.
    /// </summary>
    private async Task HandlePlexSearchRequested(string query)
    {
        if (!_jsInitialized) return;
        var offset = _context.Logic.GetNextPlexOffset();
        try
        {
            var raw = await JS.InvokeAsync<JsonElement>("plexInterop.search", query, offset, NowPlayingLogic.PlexPageSize);
            var (albums, total) = PlexJsonParser.ParseAlbumPage(raw);
            await _context.Logic.OnPlexAlbumResultsAsync(query, albums, total);
        }
        catch (JSDisconnectedException)
        {
            // Circuit is disposing — nothing to do.
        }
        catch (OperationCanceledException)
        {
            // Circuit is disposing — nothing to do.
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Plex album search failed for {Query} offset={Offset}", query, offset);
            await _context.Logic.OnPlexSearchFailedAsync(query);
        }
    }

    /// <summary>
    /// Invoked from JS by the IntersectionObserver when the bottom-of-list sentinel
    /// scrolls into view. Triggers another page; Logic's own guards make this idempotent.
    /// </summary>
    [JSInvokable]
    public Task LoadMorePlexAlbums() => _context.Logic.LoadMoreDtsAlbumsAsync();

    private async Task HandleAlbumSelectionBroadcast(string albumRatingKey)
    {
        if (!_jsInitialized) return;
        await JS.InvokeVoidAsync("syncAudio.selectAlbum", albumRatingKey);
    }

    private async Task HandleTrackSelectionBroadcast(string trackId)
    {
        if (!_jsInitialized) return;
        var albumKey = _context.State.PlexCurrentAlbumRatingKey ?? "";
        var track = _context.State.CurrentTrack;
        if (track is not null && track.Id == trackId)
            await JS.InvokeVoidAsync("syncAudio.selectTrackWithMeta",
                trackId, albumKey, track.Title, track.Artist, track.CoverUrl ?? "");
        else
            await JS.InvokeVoidAsync("syncAudio.selectTrack", trackId);
    }

    private async Task HandleFormatSelectionBroadcast(string format)
    {
        if (!_jsInitialized) return;
        await JS.InvokeVoidAsync("syncAudio.selectFormat", format);
    }

    [JSInvokable]
    public Task OnRemoteAlbumSelected(string albumRatingKey)
        => _context.Logic.MirrorRemoteAlbumSelectionAsync(albumRatingKey);

    [JSInvokable]
    public async Task<bool> OnRemoteTrackSelected(string trackId)
    {
        if (string.IsNullOrEmpty(trackId)) return false;
        var isSame = string.Equals(_context.State.CurrentTrack?.Id, trackId, StringComparison.Ordinal);
        if (!isSame)
            await _context.Logic.MirrorRemoteTrackSelectionAsync(trackId);
        else
            // Same track but pipeline may not have run yet (e.g. initial split skipped
            // because the machine was Disconnected). Ensure buffering starts so this
            // device can report readiness to the hub.
            await _context.Logic.EnsureReadyForPlayAsync();
        return isSame;
    }

    [JSInvokable]
    public Task OnRemoteFormatSelected(string format)
        => _context.Logic.MirrorRemoteFormatChangeAsync(format);

    [JSInvokable]
    public async Task OnRemoteTrackSelectedWithMeta(
        string trackId, string albumKey, string title, string artist, string coverUrl)
    {
        if (!string.IsNullOrEmpty(albumKey)
            && !string.Equals(_context.State.PlexCurrentAlbumRatingKey, albumKey, StringComparison.Ordinal))
            await _context.Logic.MirrorRemoteAlbumSelectionAsync(albumKey);

        if (!string.IsNullOrEmpty(trackId))
        {
            if (!string.Equals(_context.State.CurrentTrack?.Id, trackId, StringComparison.Ordinal))
                await _context.Logic.MirrorRemoteTrackSelectionAsync(trackId);
            else
                await _context.Logic.EnsureReadyForPlayAsync();
        }
    }

    /// <summary>
    /// User picked an album. Fetch its track listing through the browser (cookie carried),
    /// then hand the tracks back to Logic to enqueue and start playing the first.
    /// </summary>
    private async Task HandlePlexAlbumPlayRequested(string ratingKey)
    {
        if (!_jsInitialized) return;
        try
        {
            var raw = await JS.InvokeAsync<JsonElement>("plexInterop.getAlbumTracks", ratingKey);
            var tracks = PlexJsonParser.ParseTracks(raw);
            await _context.Logic.OnPlexAlbumTracksAsync(ratingKey, tracks);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Plex album-tracks fetch failed for {RatingKey}", ratingKey);
            await _context.Logic.OnPlexAlbumTracksFailedAsync(ratingKey, ex.Message);
        }
    }

    /// <summary>
    /// Logic asked us to prepare a Plex track (run the split via /plex/prepare on the
    /// browser side so the HttpOnly cookie is carried).
    /// </summary>
    private async Task HandlePlexPrepareRequested(Track track)
    {
        if (!_jsInitialized) return;
        try
        {
            var ov = _context.State.ChannelMappingOverride;
            var payload = new
            {
                id = track.Id,
                title = track.Title,
                artist = track.Artist,
                album = track.Album,
                remoteSourceUrl = track.RemoteSourceUrl ?? track.AudioUrl,
                frontL = (int?)ov?.FrontL,
                frontR = (int?)ov?.FrontR,
                backL = (int?)ov?.BackL,
                backR = (int?)ov?.BackR,
                outputFormat = _context.State.OutputFormat,
            };
            var raw = await JS.InvokeAsync<JsonElement>("plexInterop.prepare", payload);
            if (raw.TryGetProperty("error", out var err))
            {
                await _context.Logic.OnPlexSplitFailedAsync(track.Id, err.GetString() ?? "unknown");
                return;
            }
            var frontUrl = raw.GetProperty("frontUrl").GetString() ?? string.Empty;
            var backUrl = raw.GetProperty("backUrl").GetString() ?? string.Empty;

            int? channelCount = raw.TryGetProperty("channelCount", out var ccEl) && ccEl.ValueKind == JsonValueKind.Number
                ? ccEl.GetInt32() : null;
            string? channelLayout = raw.TryGetProperty("channelLayout", out var clEl) && clEl.ValueKind == JsonValueKind.String
                ? clEl.GetString() : null;
            ChannelMapping? eff = null;
            if (raw.TryGetProperty("mapping", out var mapEl) && mapEl.ValueKind == JsonValueKind.Object)
            {
                eff = new ChannelMapping(
                    mapEl.GetProperty("frontL").GetInt32(),
                    mapEl.GetProperty("frontR").GetInt32(),
                    mapEl.GetProperty("backL").GetInt32(),
                    mapEl.GetProperty("backR").GetInt32());
            }

            await _context.Logic.OnPlexSplitReadyAsync(track.Id, frontUrl, backUrl, channelCount, channelLayout, eff);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Plex prepare failed for {TrackId}", track.Id);
            await _context.Logic.OnPlexSplitFailedAsync(track.Id, ex.Message);
        }
    }

    protected async Task HandlePlexConnected()
    {
        // After link succeeded the cookie is set on the browser side. We can't read
        // it from the SignalR circuit, but the user identity will appear after a page
        // refresh. SetPlexConnectionStateAsync auto-triggers the DTS album load.
        await _context.Logic.SetPlexConnectionStateAsync(true, _context.State.PlexUsername);
    }

    protected async Task HandlePlexDisconnected()
    {
        await _context.Logic.SetPlexConnectionStateAsync(false, null);
    }

    protected async Task HandleUnlockTap()
    {
        if (_jsInitialized)
            await JS.InvokeVoidAsync("syncAudio.unlockAndPlay");
    }

    // ─── JS → .NET ──────────────────────────────────────────────────

    [JSInvokable]
    public Task ShowUnlockPrompt(bool show)
    {
        _context.State.ShowUnlockPrompt = show;
        return InvokeAsync(StateHasChanged);
    }

    [JSInvokable]
    public Task UpdateProgress(double p)
    {
        _context.Logic.OnBufferedTick(p);
        return OnStateChangedAsync();
    }

    [JSInvokable]
    public Task UpdatePosition(double position, double duration)
    {
        _context.Logic.OnPositionTick(position, duration);
        return OnStateChangedAsync();
    }

    [JSInvokable]
    public Task UpdatePlayState(bool isPlaying) => _context.Logic.OnPlayStateChangedAsync(isPlaying);

    [JSInvokable]
    public Task UpdateTrackEnded() => _context.Logic.OnTrackEndedNaturally();

    [JSInvokable]
    public Task UpdateReady(bool isReady) => _context.Logic.OnReadyChangedAsync(isReady);

    [JSInvokable]
    public Task UpdateDecodeError(string message) => _context.Logic.OnDecodeErrorAsync(message);

    [JSInvokable]
    public Task UpdateLatencyOffset(int offsetMs)
        => _context.Logic.OnLatencyOffsetChangedAsync(offsetMs);

    [JSInvokable]
    public Task UpdateOffset(long offsetMs)
    {
        _context.Logic.ApplyOffset(offsetMs);
        return OnStateChangedAsync();
    }

    [JSInvokable]
    public Task UpdateMembers(int n)
    {
        _context.Logic.ApplyMemberCount(n);
        return OnStateChangedAsync();
    }

    [JSInvokable]
    public Task UpdateAudioInfo(int channelCount, int sampleRate, int left, int right)
    {
        _context.Logic.ApplyAudioInfo(channelCount, left, right);
        return OnStateChangedAsync();
    }

    /// <summary>
    /// Fired by the hub on the caller (the user who hit Play) when the adaptive
    /// lead-time calculator hit its cap — the slowest peer is too far behind for
    /// the broadcast to reach them in time.
    /// </summary>
    [JSInvokable]
    public Task UpdateWaitingForPeers(int remainingMs, double fraction)
        => _context.Logic.OnWaitingForPeersAsync();

    /// <summary>
    /// Receives the full peer-state list from the hub (via JS). Updates State.Peers
    /// and re-renders so the loading overlay reflects current group status.
    /// </summary>
    [JSInvokable]
    public async Task UpdatePeersState(JsonElement peers)
    {
        var list = new List<NowPlayingState.PeerState>();
        foreach (var p in peers.EnumerateArray())
        {
            var name     = p.TryGetProperty("name",     out var n)  ? n.GetString()  ?? "Device" : "Device";
            var phase    = p.TryGetProperty("phase",    out var ph) ? ph.GetString() ?? "idle"   : "idle";
            var progress = p.TryGetProperty("progress", out var pr) ? pr.GetDouble() : 0.0;
            var isSelf   = p.TryGetProperty("isSelf",   out var sf) && sf.GetBoolean();
            // Server phase lags by a round-trip after _reportPhase('playing') is sent.
            // Use local machine as authoritative source for our own entry.
            if (isSelf && _context.State.Machine.IsPlaying) phase = "playing";
            var trackId  = p.TryGetProperty("trackId",  out var ti) ? ti.GetString() ?? "" : "";
            var title    = p.TryGetProperty("title",    out var tl) ? tl.GetString() ?? "" : "";
            var artist   = p.TryGetProperty("artist",   out var ar) ? ar.GetString() ?? "" : "";
            var coverUrl = p.TryGetProperty("coverUrl", out var cu) ? cu.GetString() ?? "" : "";
            list.Add(new NowPlayingState.PeerState(name, phase, progress, isSelf, trackId, title, artist, coverUrl));
        }
        _context.State.Peers = list;
        await OnStateChangedAsync();
    }

    [JSInvokable]
    public async Task OnScheduledPlay(double playAtMs, string trackId)
    {
        var currentId = _context.State.CurrentTrack?.Id ?? string.Empty;

        if (string.IsNullOrEmpty(trackId) ||
            string.Equals(currentId, trackId, StringComparison.Ordinal))
        {
            await JS.InvokeVoidAsync("syncAudio.schedulePlay", playAtMs);
            return;
        }

        var track = _context.State.Library.FirstOrDefault(t => t.Id == trackId)
                    ?? _context.State.Queue.FirstOrDefault(t => t.Id == trackId);

        if (track is null)
        {
            Logger.LogWarning("Remote play for unknown track {TrackId}; scheduling on current buffer", trackId);
            await JS.InvokeVoidAsync("syncAudio.schedulePlay", playAtMs);
            return;
        }

        await _context.Logic.SwitchTrackForRemotePlayAsync(track, playAtMs);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _context.State.OnStateChanged -= OnStateChangedAsync;

        try
        {
            if (_jsInitialized) await JS.InvokeVoidAsync("syncAudio.requestStop");
        }
        catch
        {
            // circuit may be gone
        }

        if (_plexObserverInstalled)
        {
            try { await JS.InvokeVoidAsync("plexInterop.uninstallLoadMoreObserver", PlexSheet.LoadMoreSentinelId); }
            catch { /* circuit gone */ }
        }

        _toastDismissCts?.Cancel();
        _toastDismissCts?.Dispose();
        _selfRef?.Dispose();
    }
}
