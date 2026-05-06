using System.Text.Json;
using SyncAudio.Models;

namespace SyncAudio.Components.Pages.NowPlaying;

internal static class PlexJsonParser
{
    public static (IReadOnlyList<PlexAlbumSummary> Albums, int Total) ParseAlbumPage(JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object) return ([], 0);
        var total = json.TryGetProperty("total", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : 0;
        var albumsEl = json.TryGetProperty("albums", out var ae) ? ae : default;
        return (ParseAlbums(albumsEl), total);
    }

    public static IReadOnlyList<PlexAlbumSummary> ParseAlbums(JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Array) return [];
        var list = new List<PlexAlbumSummary>(json.GetArrayLength());
        foreach (var a in json.EnumerateArray())
        {
            try
            {
                int? year = a.TryGetProperty("year", out var y) && y.ValueKind == JsonValueKind.Number ? y.GetInt32() : null;
                var trackCount = a.TryGetProperty("trackCount", out var tc) && tc.ValueKind == JsonValueKind.Number ? tc.GetInt32() : 0;
                list.Add(new PlexAlbumSummary(
                    RatingKey: a.GetProperty("ratingKey").GetString() ?? string.Empty,
                    Title: a.TryGetProperty("title", out var ti) ? ti.GetString() ?? string.Empty : string.Empty,
                    Artist: a.TryGetProperty("artist", out var ar) ? ar.GetString() ?? string.Empty : string.Empty,
                    Year: year,
                    TrackCount: trackCount,
                    CoverUrl: a.TryGetProperty("coverUrl", out var cu) && cu.ValueKind == JsonValueKind.String ? cu.GetString() : null
                ));
            }
            catch { /* skip malformed entry */ }
        }
        return list;
    }

    public static IReadOnlyList<Track> ParseTracks(JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Array) return [];
        var list = new List<Track>(json.GetArrayLength());
        foreach (var t in json.EnumerateArray())
        {
            try
            {
                var palette = new List<string>();
                if (t.TryGetProperty("palette", out var p) && p.ValueKind == JsonValueKind.Array)
                {
                    foreach (var c in p.EnumerateArray())
                    {
                        if (c.ValueKind == JsonValueKind.String) palette.Add(c.GetString() ?? "#7A3CFF");
                    }
                }
                if (palette.Count == 0) palette.AddRange(new[] { "#5EC8FF", "#7A3CFF", "#FF8FB1" });

                var durationSecs = t.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
                    ? d.GetDouble()
                    : 0.0;

                list.Add(new Track(
                    Id: t.GetProperty("id").GetString() ?? string.Empty,
                    Title: t.TryGetProperty("title", out var ti) ? ti.GetString() ?? string.Empty : string.Empty,
                    Artist: t.TryGetProperty("artist", out var ar) ? ar.GetString() ?? string.Empty : string.Empty,
                    Album: t.TryGetProperty("album", out var al) ? al.GetString() ?? string.Empty : string.Empty,
                    AudioUrl: t.TryGetProperty("audioUrl", out var au) ? au.GetString() ?? string.Empty : string.Empty,
                    Duration: TimeSpan.FromSeconds(durationSecs),
                    Palette: palette,
                    Lossless: t.TryGetProperty("lossless", out var ll) && ll.GetBoolean(),
                    Spatial: t.TryGetProperty("spatial", out var sp) && sp.GetBoolean(),
                    CoverUrl: t.TryGetProperty("coverUrl", out var cu) && cu.ValueKind == JsonValueKind.String ? cu.GetString() : null,
                    Source: TrackSource.Plex,
                    RemoteSourceUrl: t.TryGetProperty("remoteSourceUrl", out var ru) && ru.ValueKind == JsonValueKind.String ? ru.GetString() : null
                ));
            }
            catch { /* skip malformed entry */ }
        }
        return list;
    }
}
