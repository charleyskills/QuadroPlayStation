using SyncAudio.Core.Models;

namespace SyncAudio.Core.Services.Plex;

public interface IPlexLibraryService
{
    bool IsConnected { get; }
    Task<PlexSession?> EnsureSessionAsync(CancellationToken ct = default);

    /// <summary>
    /// Paged title-filtered album search against the user's first music library section.
    /// Returns the slice plus the absolute <c>total</c> available so the caller can
    /// decide whether more pages exist.
    /// </summary>
    Task<(IReadOnlyList<PlexAlbumSummary> Items, int Total)> SearchAlbumsAsync(
        string query, int offset, int size, CancellationToken ct = default);

    Task<IReadOnlyList<Track>> GetAlbumTracksAsync(string albumRatingKey, CancellationToken ct = default);
    string? GetAuthToken();
}
