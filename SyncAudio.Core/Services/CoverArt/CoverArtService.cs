using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using SyncAudio.Core.Services.Storage;

namespace SyncAudio.Core.Services.CoverArt;

public sealed class CoverArtService(
    IObjectStore objectStore,
    IOptions<StorageOptions> storageOptions,
    IMemoryCache memCache,
    ILogger<CoverArtService> logger) : ICoverArtService
{
    private static readonly TimeSpan CacheSlidingExpiry = TimeSpan.FromMinutes(30);
    private string CoverBucket => storageOptions.Value.Buckets.Covers;

    public bool PrimeFromFile(string trackId, string filePath)
    {
        try
        {
            using var tagFile = TagLib.File.Create(filePath);
            var pictures = tagFile.Tag.Pictures;
            if (pictures is null || pictures.Length == 0) return false;

            var pic = pictures[0];
            var data = pic.Data?.Data;
            if (data is null || data.Length == 0) return false;

            var sourceMime = !string.IsNullOrEmpty(pic.MimeType)
                ? pic.MimeType
                : CoverMime.Detect(data);

            // PrimeFromFile is called from a sync scan; LocalFileObjectStore.PutAsync
            // completes synchronously for local FS, so GetAwaiter().GetResult() is safe here.
            var thumbKey = ThumbKey(trackId);
            if (!objectStore.ExistsAsync(CoverBucket, thumbKey).GetAwaiter().GetResult())
            {
                using var thumb = CoverProcessor.MakeThumb(data);
                objectStore.PutAsync(CoverBucket, thumbKey, thumb, "image/webp").GetAwaiter().GetResult();
            }

            var fullKey = FullKey(trackId);
            if (!objectStore.ExistsAsync(CoverBucket, fullKey).GetAwaiter().GetResult())
            {
                using var fullStream = new MemoryStream(data, writable: false);
                objectStore.PutAsync(CoverBucket, fullKey, fullStream, sourceMime).GetAwaiter().GetResult();
            }

            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to prime cover art for {TrackId} from {Path}", trackId, filePath);
            return false;
        }
    }

    public async Task<(byte[] Data, string Mime)?> GetCoverAsync(string trackId, string size, CancellationToken ct = default)
    {
        var isFull = size is "full";
        var key = isFull ? FullKey(trackId) : ThumbKey(trackId);
        var cacheKey = $"cover:{key}";

        if (memCache.TryGetValue(cacheKey, out CacheEntry cached) && cached.Data is not null)
            return (cached.Data, cached.Mime);

        if (!await objectStore.ExistsAsync(CoverBucket, key, ct))
            return null;

        await using var stream = await objectStore.OpenReadAsync(CoverBucket, key, ct);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();

        // Thumbs are always WebP; full keeps original format and is sniffed from bytes.
        var mime = isFull ? CoverMime.Detect(bytes) : "image/webp";

        memCache.Set(cacheKey, new CacheEntry(bytes, mime), new MemoryCacheEntryOptions
        {
            SlidingExpiration = CacheSlidingExpiry,
        });

        return (bytes, mime);
    }

    private static string ThumbKey(string trackId) => $"local/{trackId}_thumb.webp";
    private static string FullKey(string trackId) => $"local/{trackId}_orig";

    private readonly record struct CacheEntry(byte[]? Data, string Mime);
}
