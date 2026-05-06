using SyncAudio.Models;

namespace SyncAudio.Services.TrackSplit;

public interface ITrackSplitService
{
    /// <summary>
    /// Ensures the per-track split files exist on disk. Returns immediately
    /// (with cached URLs) if they already do; otherwise runs ffmpeg to create
    /// them. Concurrent callers for the same track + mapping wait on the same job.
    /// </summary>
    /// <param name="track">Track to split. <see cref="Track.Source"/> determines whether
    /// the splitter reads from a local file (Local) or streams via HTTPS (Plex).</param>
    /// <param name="overrideMapping">Manual channel-index override. When null, the service
    /// auto-detects channel count + layout via ffprobe and picks the conventional
    /// front/back indices for that layout.</param>
    /// <param name="authHeaderValue">For remote sources, the value of an HTTP header to send to the
    /// origin (e.g. <c>X-Plex-Token: ...</c>). Ignored for local sources. Pass null when no
    /// auth is required.</param>
    Task<TrackSplitResult> EnsureSplitAsync(
        Track track,
        ChannelMapping? overrideMapping = null,
        string? authHeaderValue = null,
        CancellationToken ct = default);
}
