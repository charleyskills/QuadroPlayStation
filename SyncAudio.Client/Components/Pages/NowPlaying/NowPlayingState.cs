using SyncAudio.Client.Components.Pages.NowPlaying.StateMachines;
using SyncAudio.Core.Services;
using SyncAudio.Core.Models;
using SyncAudio.Core.Services.TrackSplit;

namespace SyncAudio.Client.Components.Pages.NowPlaying;

public sealed class NowPlayingState
{
    private static readonly IReadOnlyList<string> DefaultPalette =
        ["#FF5E7E", "#7A3CFF", "#FF9E5E"];

    /// <summary>
    /// Playback lifecycle state machine. Exposes <c>IsJoined</c>, <c>IsSplitting</c>,
    /// <c>IsBuffering</c>, <c>IsReady</c>, <c>IsPlaying</c>, and <c>IsIdle</c> directly.
    /// Public so <see cref="NowPlayingLogic"/> can fire triggers; do not mutate state directly.
    /// </summary>
    public LocalDeviceStateMachine Machine { get; }

    public NowPlayingState()
    {
        Machine = new LocalDeviceStateMachine();
        // Auto-clear the WaitingForPeers advisory when playback actually begins.
        Machine.PlaybackBegan += () => IsWaitingForPeers = false;
    }

    public event Func<Task>? OnStateChanged;

    private Task Notify() => OnStateChanged?.Invoke() ?? Task.CompletedTask;

    private static void SetProperty<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
    }

    private void SetPropertyAndNotify<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        _ = Notify();
    }

    public NowPlayingConfiguration Configuration { get; init; } = NowPlayingConfiguration.Default;

    public IReadOnlyList<Track> Library
    {
        get;
        set => SetProperty(ref field, value);
    } = [];

    public Track? CurrentTrack
    {
        get;
        set => SetProperty(ref field, value);
    }

    public string? CoverSlot0 { get; private set; }
    public string? CoverSlot1 { get; private set; }
    public int ActiveCoverSlot { get; private set; }

    internal CurrentCoverTracker? CoverTracker { private get; init; }

    internal void UpdateCoverUrl(string? newUrl)
    {
        var fullUrl = NowPlayingHelpers.AsFullCover(newUrl);
        if (ActiveCoverSlot == 0)
        {
            CoverSlot1 = fullUrl;
            ActiveCoverSlot = 1;
        }
        else
        {
            CoverSlot0 = fullUrl;
            ActiveCoverSlot = 0;
        }
        CoverTracker?.CoverUrl = newUrl;
    }

    public IReadOnlyList<Track> Queue
    {
        get;
        set => SetProperty(ref field, value);
    } = [];

    public IReadOnlyList<Track> AlbumTracks
    {
        get;
        set => SetProperty(ref field, value);
    } = [];

    internal readonly List<string> HistoryIds = new(8);

    /// <summary>
    /// Hub fired WaitingForPeers because the adaptive lead would have exceeded its cap —
    /// the slowest peer probably won't be ready in time, so playback may drift on that
    /// device. Auto-clears when playback actually starts (the <see cref="LocalDeviceStateMachine.PlaybackBegan"/>
    /// event subscribes in the ctor). Set by the JS-side <c>UpdateWaitingForPeers</c> hub callback.
    /// </summary>
    public bool IsWaitingForPeers
    {
        get;
        set => SetProperty(ref field, value);
    }

    public bool ShowUnlockPrompt
    {
        get;
        set => SetProperty(ref field, value);
    }

    public record ConnectionToast(bool IsError, string Message);

    public ConnectionToast? ActiveConnectionToast
    {
        get;
        set => SetPropertyAndNotify(ref field, value);
    }

    public double PositionSeconds
    {
        get;
        set => SetProperty(ref field, value);
    }

    public double DurationSeconds
    {
        get;
        set => SetProperty(ref field, value);
    }

    public double BufferedProgress
    {
        get;
        set => SetProperty(ref field, value);
    }

    public double Volume
    {
        get;
        set => SetProperty(ref field, Math.Clamp(value, 0.0, 1.0));
    } = 0.55;

    public string GroupName
    {
        get;
        set => SetPropertyAndNotify(ref field, value);
    } = "demo";

    public int MemberCount
    {
        get;
        set => SetProperty(ref field, value);
    }

    public long OffsetMs
    {
        get;
        set => SetProperty(ref field, value);
    }

    // Per-device manual latency offset (ms). Mirrors the JS-side persisted value;
    // updated from JS via UpdateLatencyOffset. Positive = play earlier on this device.
    public int LatencyOffsetMs
    {
        get;
        set => SetProperty(ref field, value);
    }

    public int Channels
    {
        get;
        set => SetProperty(ref field, value);
    }

    public int LeftChannel
    {
        get;
        set => SetProperty(ref field, value);
    }

    public int RightChannel
    {
        get;
        set => SetProperty(ref field, value);
    } = 1;

    public bool QueueOpen
    {
        get;
        set => SetPropertyAndNotify(ref field, value);
    }

    public bool PlexPanelOpen
    {
        get;
        set => SetPropertyAndNotify(ref field, value);
    }

    public bool DiscoverViewOpen
    {
        get;
        set => SetPropertyAndNotify(ref field, value);
    }

    /// <summary>
    /// Which pre-split stereo stream this device is currently playing.
    /// "Front" → front-left + front-right pair. "Back" → back/surround pair.
    /// Each device picks one; the two together form a quadro experience.
    /// </summary>
    public string SelectedStreamPair
    {
        get;
        set => SetPropertyAndNotify(ref field, value);
    } = "Front";

    /// <summary>URL of the front-pair audio file produced by ffmpeg for the current track.
    /// Extension matches <see cref="OutputFormat"/>. Null until the split completes.</summary>
    public string? FrontStreamUrl
    {
        get;
        set => SetProperty(ref field, value);
    }

    /// <summary>URL of the back-pair audio file produced by ffmpeg for the current track.
    /// Extension matches <see cref="OutputFormat"/>. Null until the split completes.</summary>
    public string? BackStreamUrl
    {
        get;
        set => SetProperty(ref field, value);
    }

    /// <summary>
    /// Audio codec used for the per-track ffmpeg split: "mp3" (default, lossy CBR),
    /// "opus256" (Opus 256 kbps VBR in OGG), or "flac" (lossless). Group-wide preference
    /// — when this device changes it, all peers in the group resplit and restart together.
    /// </summary>
    public string OutputFormat
    {
        get;
        set => SetPropertyAndNotify(ref field, value);
    } = "opus256";

    /// <summary>True while the FORMAT disclosure pane is expanded under the toolbar.</summary>
    public bool FormatOpen
    {
        get;
        set => SetPropertyAndNotify(ref field, value);
    }

    /// <summary>Number of audio channels in the current track's source, as reported by ffprobe. Null until the split returns.</summary>
    public int? SourceChannelCount
    {
        get;
        set => SetProperty(ref field, value);
    }

    /// <summary>ffprobe channel_layout string for the source (e.g. "5.1(side)", "7.1", "stereo"). Null when unknown.</summary>
    public string? SourceChannelLayout
    {
        get;
        set => SetProperty(ref field, value);
    }

    /// <summary>
    /// User-supplied channel mapping override. Null means "let the splitter auto-pick from layout".
    /// Session-only — never persisted.
    /// </summary>
    public ChannelMapping? ChannelMappingOverride
    {
        get;
        set => SetPropertyAndNotify(ref field, value);
    }

    /// <summary>The mapping the splitter actually used for the current split (auto or override).</summary>
    public ChannelMapping? EffectiveChannelMapping
    {
        get;
        set => SetProperty(ref field, value);
    }

    /// <summary>The audio URL this device should currently load — derived from the chosen pair.</summary>
    public string? CurrentStreamUrl => SelectedStreamPair switch
    {
        "Back" => BackStreamUrl,
        _ => FrontStreamUrl,
    };

    public bool ChannelMapOpen
    {
        get;
        set => SetPropertyAndNotify(ref field, value);
    } = true;

    // ─── PLEX STATE ───────────────────────────────────────────────────

    public bool IsPlexConnected
    {
        get;
        set => SetProperty(ref field, value);
    }

    public string? PlexUsername
    {
        get;
        set => SetProperty(ref field, value);
    }

    public string PlexSearchQuery
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    public bool IsPlexSearching
    {
        get;
        set => SetProperty(ref field, value);
    }

    public IReadOnlyList<PlexAlbumSummary> PlexAlbumResults
    {
        get;
        set => SetProperty(ref field, value);
    } = [];

    /// <summary>Total matching albums on the Plex server (vs. how many we've loaded so far).</summary>
    public int PlexAlbumTotal
    {
        get;
        set => SetProperty(ref field, value);
    }

    /// <summary>True while a "load more" batch is in flight (used to debounce the IntersectionObserver).</summary>
    public bool IsPlexLoadingMore
    {
        get;
        set => SetProperty(ref field, value);
    }

    public bool HasMorePlexAlbums => PlexAlbumResults.Count < PlexAlbumTotal;

    public bool IsPlexLoadingAlbum
    {
        get;
        set => SetProperty(ref field, value);
    }

    public string? PlexCurrentAlbumRatingKey { get; set; }

    public double ParallaxX
    {
        get;
        set => SetProperty(ref field, value);
    }

    public double ParallaxY
    {
        get;
        set => SetProperty(ref field, value);
    }

    // ─── COMPUTED ──────────────────────────────────────────────────────

    public IReadOnlyList<string> ActivePalette =>
        CurrentTrack?.Palette is { Count: >= 3 } p ? p : DefaultPalette;

    public Track? UpNext => Queue.Count > 0 ? Queue[0] : null;

    public bool CanTransport => Machine.IsJoined && CurrentTrack is not null;
    public bool CanPrevious => HistoryIds.Count > 0;
    public bool CanNext => Queue.Count > 0;

    public double ProgressFraction =>
        DurationSeconds > 0 ? Math.Clamp(PositionSeconds / DurationSeconds, 0.0, 1.0) : 0.0;

    public string ElapsedDisplay => FormatTime(PositionSeconds);

    public string RemainingDisplay =>
        DurationSeconds > 0
            ? "−" + FormatTime(Math.Max(0, DurationSeconds - PositionSeconds))
            : "−:—";

    public string DurationDisplay =>
        DurationSeconds > 0 ? FormatTime(DurationSeconds) : (CurrentTrack?.DurationDisplay ?? "—:—");

    /// <summary>
    /// True when this device has committed to playing (state ≠ Idle) and at least one peer
    /// is still splitting or buffering. Cold-start (Idle) stays false so PLAY is never
    /// blocked before the user has initiated anything.
    /// </summary>
    public bool AnyPeerLoading =>
        Machine.IsJoined && !Machine.IsIdle &&
        Peers.Any(p => !p.IsSelf && p.Phase is "splitting" or "buffering");

    public record PeerState(string Name, string Phase, double Progress, bool IsSelf,
        string TrackId = "", string Title = "", string Artist = "", string CoverUrl = "");

    public IReadOnlyList<PeerState> Peers
    {
        get;
        set => SetProperty(ref field, value);
    } = [];

    /// <summary>True while joined and at least one peer is still loading (pre-playing).</summary>
    public bool ShowPeersPanel =>
        Machine.IsJoined &&
        Peers.Any(p => p.Phase is "splitting" or "buffering" or "ready");

    public bool IsSpatial => CurrentTrack?.Spatial == true || Channels >= 4;
    public bool IsLossless => CurrentTrack?.Lossless == true;

    public string SheetClass => QueueOpen ? "sheet open" : "sheet";
    public string ScrimClass => QueueOpen ? "scrim visible" : "scrim";
    public string PlexSheetClass => PlexPanelOpen ? "sheet open" : "sheet";
    public string PlexScrimClass => PlexPanelOpen ? "scrim visible" : "scrim";

    private static string FormatTime(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
        var total = (int)Math.Floor(seconds);
        var m = total / 60;
        var s = total % 60;
        return $"{m}:{s:D2}";
    }
}
