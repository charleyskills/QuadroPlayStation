using System.Collections.Concurrent;

namespace SyncAudio.Services;

public sealed class CoverArtService(ILogger<CoverArtService> logger) : ICoverArtService
{
    private readonly ConcurrentDictionary<string, CoverEntry> _cache = new();

    private readonly record struct CoverEntry(byte[]? Data, string? Mime);

    public bool PrimeFromFile(string trackId, string filePath)
    {
        try
        {
            using var tagFile = TagLib.File.Create(filePath);
            var pictures = tagFile.Tag.Pictures;
            if (pictures is null || pictures.Length == 0)
            {
                _cache[trackId] = default;
                return false;
            }

            var pic = pictures[0];
            var data = pic.Data?.Data;
            if (data is null || data.Length == 0)
            {
                _cache[trackId] = default;
                return false;
            }

            var mime = string.IsNullOrEmpty(pic.MimeType) ? "image/jpeg" : pic.MimeType;
            _cache[trackId] = new CoverEntry(data, mime);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read cover art from {Path}", filePath);
            _cache[trackId] = default;
            return false;
        }
    }

    public bool TryGetCover(string trackId, out byte[] data, out string mime)
    {
        if (_cache.TryGetValue(trackId, out var entry) && entry is { Data: { Length: > 0 } d, Mime: { } m })
        {
            data = d;
            mime = m;
            return true;
        }

        data = [];
        mime = string.Empty;
        return false;
    }
}
