using System.Net;
using SyncAudio.Models;

namespace SyncAudio.Services.Plex;

public sealed class PlexLibraryService(
    IPlexSessionStore sessionStore,
    IPlexServerClient server,
    ILogger<PlexLibraryService> logger)
    : IPlexLibraryService
{
    private static readonly string[] PlexPalette = ["#5EC8FF", "#7A3CFF", "#FF8FB1"];

    public bool IsConnected => sessionStore.Get() is not null;

    public string? GetAuthToken() => sessionStore.Get()?.Token;

    public async Task<PlexSession?> EnsureSessionAsync(CancellationToken ct = default)
    {
        var session = sessionStore.Get();
        if (session is null) return null;

        if (!string.IsNullOrEmpty(session.BaseUri)
            && !string.IsNullOrEmpty(session.MachineIdentifier)
            && !string.IsNullOrEmpty(session.MusicSectionKey))
        {
            return session;
        }

        // Hydrate the session by picking a server that hosts a music library.
        try
        {
            var resources = await server.GetResourcesAsync(session.Token, ct);
            foreach (var res in resources.Where(r => r.Provides?.Contains("server", StringComparison.OrdinalIgnoreCase) == true))
            {
                var pickedUri = PickConnection(res);
                if (pickedUri is null) continue;
                var serverToken = string.IsNullOrEmpty(res.AccessToken) ? session.Token : res.AccessToken;

                IReadOnlyList<PlexLibrarySection> sections;
                try
                {
                    sections = await server.GetMusicSectionsAsync(pickedUri, serverToken, ct);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Skipping Plex server {Name}: section probe failed", res.Name);
                    continue;
                }

                if (sections.Count == 0) continue;

                var hydrated = session with
                {
                    Token = serverToken,
                    BaseUri = pickedUri,
                    MachineIdentifier = res.ClientIdentifier,
                    MusicSectionKey = sections[0].Key,
                };
                sessionStore.Set(hydrated);
                logger.LogInformation("Selected Plex server {Name} at {BaseUri} (music section {SectionKey})",
                    res.Name, pickedUri, sections[0].Key);
                return hydrated;
            }
            logger.LogWarning("No Plex server with a music library was found for the linked account");
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            logger.LogWarning("Plex token rejected; clearing session");
            sessionStore.Clear();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to hydrate Plex session");
        }

        return null;
    }

    public async Task<(IReadOnlyList<PlexAlbumSummary> Items, int Total)> SearchAlbumsAsync(
        string query, int offset, int size, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return ([], 0);
        var session = await EnsureSessionAsync(ct);
        if (session is null || string.IsNullOrEmpty(session.BaseUri) || string.IsNullOrEmpty(session.MusicSectionKey))
        {
            return ([], 0);
        }

        try
        {
            var (hits, total) = await server.SearchAlbumsByTitleAsync(
                session.BaseUri, session.Token, session.MusicSectionKey, query,
                offset, size, ct);
            return (hits.Select(MapAlbum).ToList(), total);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            logger.LogWarning("Plex token rejected during search; clearing session");
            sessionStore.Clear();
            return ([], 0);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Plex album search failed for query '{Query}' (offset={Offset}, size={Size})",
                query, offset, size);
            return ([], 0);
        }
    }

    public async Task<IReadOnlyList<Track>> GetAlbumTracksAsync(string albumRatingKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(albumRatingKey)) return [];
        var session = await EnsureSessionAsync(ct);
        if (session is null || string.IsNullOrEmpty(session.BaseUri)) return [];

        try
        {
            var hits = await server.GetAlbumTracksAsync(session.BaseUri, session.Token, albumRatingKey, ct);
            return hits.Select(h => MapTrack(h, session)).ToList();
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            logger.LogWarning("Plex token rejected fetching album tracks; clearing session");
            sessionStore.Clear();
            return [];
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Plex GetAlbumTracks failed for {RatingKey}", albumRatingKey);
            return [];
        }
    }

    private static PlexAlbumSummary MapAlbum(PlexAlbumHit hit) => new(
        RatingKey: hit.RatingKey,
        Title: string.IsNullOrEmpty(hit.Title) ? "(untitled)" : hit.Title,
        Artist: string.IsNullOrEmpty(hit.Artist) ? "Unknown" : hit.Artist,
        Year: hit.Year,
        TrackCount: hit.TrackCount,
        CoverUrl: hit.ThumbKey is null ? null : $"/plex/cover/{Uri.EscapeDataString(hit.RatingKey)}?thumb={Uri.EscapeDataString(hit.ThumbKey)}");

    private static Track MapTrack(PlexTrackHit hit, PlexSession session)
    {
        var partUrl = $"{session.BaseUri.TrimEnd('/')}{hit.PartKey}?download=1";
        var id = $"plex-{hit.RatingKey}";
        var lossless = string.Equals(hit.Container, "flac", StringComparison.OrdinalIgnoreCase)
            || string.Equals(hit.Container, "alac", StringComparison.OrdinalIgnoreCase);

        return new Track(
            Id: id,
            Title: string.IsNullOrEmpty(hit.Title) ? "(untitled)" : hit.Title,
            Artist: string.IsNullOrEmpty(hit.Artist) ? "Unknown" : hit.Artist,
            Album: hit.Album,
            AudioUrl: partUrl,
            Duration: hit.Duration,
            Palette: PlexPalette,
            Lossless: lossless,
            Spatial: false,
            CoverUrl: hit.ThumbKey is null ? null : $"/plex/cover/{Uri.EscapeDataString(hit.RatingKey)}?thumb={Uri.EscapeDataString(hit.ThumbKey)}",
            Source: TrackSource.Plex,
            RemoteSourceUrl: partUrl);
    }

    private static string? PickConnection(PlexResource res)
    {
        if (res.Connections is null || res.Connections.Count == 0) return null;
        // Priority: local https → local http → remote https (non-relay) → relay.
        var ordered = res.Connections
            .OrderByDescending(c => c.Local && string.Equals(c.Protocol, "https", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(c => c.Local)
            .ThenByDescending(c => string.Equals(c.Protocol, "https", StringComparison.OrdinalIgnoreCase) && !c.Relay)
            .ThenBy(c => c.Relay);
        return ordered.FirstOrDefault()?.Uri;
    }
}
