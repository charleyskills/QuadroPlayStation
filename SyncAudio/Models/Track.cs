namespace SyncAudio.Models;

public enum TrackSource
{
    Local,
    Plex,
}

public sealed record Track(
    string Id,
    string Title,
    string Artist,
    string Album,
    string AudioUrl,
    TimeSpan Duration,
    IReadOnlyList<string> Palette,
    bool Lossless,
    bool Spatial,
    string? CoverUrl = null,
    TrackSource Source = TrackSource.Local,
    string? RemoteSourceUrl = null)
{
    public string DurationDisplay => Duration <= TimeSpan.Zero
        ? "—:—"
        : $"{(int)Duration.TotalMinutes}:{Duration.Seconds:D2}";
}
