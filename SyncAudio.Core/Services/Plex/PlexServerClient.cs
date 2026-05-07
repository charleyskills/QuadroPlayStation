using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace SyncAudio.Core.Services.Plex;

public sealed class PlexServerClient(
    HttpClient http,
    PlexClientIdentity identity,
    IOptions<PlexOptions> options,
    ILogger<PlexServerClient> logger)
    : IPlexServerClient
{
    private readonly PlexOptions _opts = options.Value;

    public async Task<IReadOnlyList<PlexResource>> GetResourcesAsync(string token, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            "https://plex.tv/api/v2/resources?includeHttps=1&includeRelay=1");
        ApplyClientHeaders(req);
        req.Headers.Add("X-Plex-Token", token);

        using var res = await http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        var resources = await JsonSerializer.DeserializeAsync<List<PlexResource>>(stream, JsonOptions, ct);
        return resources ?? [];
    }

    public async Task<IReadOnlyList<PlexLibrarySection>> GetMusicSectionsAsync(string baseUri, string token, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUri.TrimEnd('/')}/library/sections");
        ApplyClientHeaders(req);
        req.Headers.Add("X-Plex-Token", token);

        using var res = await http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var list = new List<PlexLibrarySection>();
        if (!doc.RootElement.TryGetProperty("MediaContainer", out var mc)) return list;
        if (!mc.TryGetProperty("Directory", out var dirs) || dirs.ValueKind != JsonValueKind.Array) return list;

        foreach (var d in dirs.EnumerateArray())
        {
            var type = d.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (!string.Equals(type, "artist", StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(new PlexLibrarySection(
                Key: d.GetProperty("key").GetString() ?? string.Empty,
                Title: d.TryGetProperty("title", out var ti) ? ti.GetString() ?? string.Empty : string.Empty,
                Type: type ?? string.Empty));
        }
        return list;
    }

    public async Task<(IReadOnlyList<PlexAlbumHit> Items, int TotalSize)> SearchAlbumsByTitleAsync(
        string baseUri, string token, string sectionKey, string titleQuery,
        int offset, int size, CancellationToken ct = default)
    {
        // Plex's section-/all endpoint accepts type=9 (album), title= (case-insensitive
        // contains), and the X-Plex-Container-* pagination params. /hubs/search caps at
        // ~30 hits per type; this endpoint paginates the entire matching set.
        var url = $"{baseUri.TrimEnd('/')}/library/sections/{Uri.EscapeDataString(sectionKey)}/all" +
            $"?type=9&title={Uri.EscapeDataString(titleQuery)}" +
            $"&X-Plex-Container-Start={offset}&X-Plex-Container-Size={size}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyClientHeaders(req);
        req.Headers.Add("X-Plex-Token", token);

        using var res = await http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var items = new List<PlexAlbumHit>();
        var total = 0;
        if (!doc.RootElement.TryGetProperty("MediaContainer", out var mc)) return (items, total);
        if (mc.TryGetProperty("totalSize", out var ts) && ts.ValueKind == JsonValueKind.Number) total = ts.GetInt32();
        else if (mc.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number) total = sz.GetInt32();

        if (mc.TryGetProperty("Metadata", out var meta) && meta.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in meta.EnumerateArray())
            {
                var hit = TryMapAlbum(m);
                if (hit is not null) items.Add(hit);
            }
        }

        logger.LogDebug("Plex section albums '{Query}' offset={Offset} size={Size} → {Count}/{Total}",
            titleQuery, offset, size, items.Count, total);
        return (items, total);
    }

    public async Task<IReadOnlyList<PlexTrackHit>> GetAlbumTracksAsync(string baseUri, string token, string albumRatingKey, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"{baseUri.TrimEnd('/')}/library/metadata/{Uri.EscapeDataString(albumRatingKey)}/children");
        ApplyClientHeaders(req);
        req.Headers.Add("X-Plex-Token", token);

        using var res = await http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var hits = new List<PlexTrackHit>();
        if (!doc.RootElement.TryGetProperty("MediaContainer", out var mc)) return hits;
        if (!mc.TryGetProperty("Metadata", out var meta) || meta.ValueKind != JsonValueKind.Array) return hits;

        foreach (var m in meta.EnumerateArray())
        {
            var hit = TryMapTrack(m);
            if (hit is not null) hits.Add(hit);
        }
        return hits;
    }

    private static PlexAlbumHit? TryMapAlbum(JsonElement m)
    {
        if (m.TryGetProperty("type", out var t) && !string.Equals(t.GetString(), "album", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var ratingKey = m.TryGetProperty("ratingKey", out var rk) ? rk.GetString() : null;
        if (string.IsNullOrEmpty(ratingKey)) return null;

        var title = m.TryGetProperty("title", out var ti) ? ti.GetString() ?? string.Empty : string.Empty;
        var artist = m.TryGetProperty("parentTitle", out var pt) ? pt.GetString() ?? string.Empty : string.Empty;
        int? year = m.TryGetProperty("year", out var y) && y.ValueKind == JsonValueKind.Number ? y.GetInt32() : null;
        var trackCount = m.TryGetProperty("leafCount", out var lc) && lc.ValueKind == JsonValueKind.Number ? lc.GetInt32() : 0;
        var thumb = m.TryGetProperty("thumb", out var th) ? th.GetString() : null;

        return new PlexAlbumHit(ratingKey, title, artist, year, trackCount, thumb);
    }

    private static PlexTrackHit? TryMapTrack(JsonElement m)
    {
        if (m.TryGetProperty("type", out var t) && !string.Equals(t.GetString(), "track", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var ratingKey = m.TryGetProperty("ratingKey", out var rk) ? rk.GetString() : null;
        if (string.IsNullOrEmpty(ratingKey)) return null;

        var title = m.TryGetProperty("title", out var ti) ? ti.GetString() ?? string.Empty : string.Empty;
        var artist = m.TryGetProperty("grandparentTitle", out var gt) ? gt.GetString() ?? string.Empty : string.Empty;
        var album = m.TryGetProperty("parentTitle", out var pt) ? pt.GetString() ?? string.Empty : string.Empty;
        var duration = m.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
            ? TimeSpan.FromMilliseconds(d.GetInt64())
            : TimeSpan.Zero;
        var thumb = m.TryGetProperty("thumb", out var th) ? th.GetString() : null;

        if (!m.TryGetProperty("Media", out var media) || media.ValueKind != JsonValueKind.Array) return null;
        foreach (var med in media.EnumerateArray())
        {
            if (!med.TryGetProperty("Part", out var parts) || parts.ValueKind != JsonValueKind.Array) continue;
            foreach (var p in parts.EnumerateArray())
            {
                var key = p.TryGetProperty("key", out var pk) ? pk.GetString() : null;
                if (string.IsNullOrEmpty(key)) continue;
                var container = p.TryGetProperty("container", out var c) ? c.GetString() : null;
                return new PlexTrackHit(ratingKey, title, artist, album, duration, key, container, thumb);
            }
        }
        return null;
    }

    private void ApplyClientHeaders(HttpRequestMessage req)
    {
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Add("X-Plex-Client-Identifier", identity.Value);
        req.Headers.Add("X-Plex-Product", _opts.Product);
        req.Headers.Add("X-Plex-Version", _opts.Version);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
}
