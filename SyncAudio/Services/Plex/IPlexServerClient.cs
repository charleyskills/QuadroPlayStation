namespace SyncAudio.Services.Plex;

public interface IPlexServerClient
{
    Task<IReadOnlyList<PlexResource>> GetResourcesAsync(string token, CancellationToken ct = default);
    Task<IReadOnlyList<PlexLibrarySection>> GetMusicSectionsAsync(string baseUri, string token, CancellationToken ct = default);

    /// <summary>
    /// Paged title-filtered album listing for a given music section. Returns the slice
    /// requested via <paramref name="offset"/>/<paramref name="size"/> plus the absolute
    /// total available (from Plex's <c>totalSize</c>) so the client can decide whether
    /// to fetch more.
    /// </summary>
    Task<(IReadOnlyList<PlexAlbumHit> Items, int TotalSize)> SearchAlbumsByTitleAsync(
        string baseUri, string token, string sectionKey, string titleQuery,
        int offset, int size, CancellationToken ct = default);

    Task<IReadOnlyList<PlexTrackHit>> GetAlbumTracksAsync(string baseUri, string token, string albumRatingKey, CancellationToken ct = default);
}
