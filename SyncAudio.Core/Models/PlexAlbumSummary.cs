namespace SyncAudio.Core.Models;

public sealed record PlexAlbumSummary(
    string RatingKey,
    string Title,
    string Artist,
    int? Year,
    int TrackCount,
    string? CoverUrl);
