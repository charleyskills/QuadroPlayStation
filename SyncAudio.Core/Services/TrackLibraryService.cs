using System.Globalization;
using Microsoft.Extensions.Options;
using SyncAudio.Core.Models;
using SyncAudio.Core.Services.CoverArt;
using SyncAudio.Core.Services.Storage;

namespace SyncAudio.Core.Services;

public sealed class TrackLibraryService(
    IObjectStore objectStore,
    IOptions<StorageOptions> storageOptions,
    ILogger<TrackLibraryService> logger)
    : ITrackLibraryService
{
    private static readonly string[][] Palettes =
    [
        ["#FF5E7E", "#7A3CFF", "#FF9E5E"],
        ["#FF8FB1", "#FF5E7E", "#7A3CFF"],
        ["#5EC8FF", "#7A3CFF", "#FF5E7E"],
        ["#FFB36B", "#FF6B9C", "#7A3CFF"],
        ["#A18CFF", "#5EC8FF", "#FF8FB1"],
        ["#FFC371", "#FF5F6D", "#7A3CFF"],
        ["#FF6B9C", "#FFB36B", "#5EC8FF"],
    ];

    private Task<IReadOnlyList<Track>>? _scanTask;
    private readonly Lock _lock = new();

    public Task<IReadOnlyList<Track>> GetAllAsync()
    {
        logger.LogInformation("TrackLibraryService.GetAllAsync: entry, scanTask={State}",
            _scanTask is null ? "not started" : _scanTask.Status.ToString());

        lock (_lock)
        {
            _scanTask ??= ScanBucketAsync(CancellationToken.None);
        }

        logger.LogInformation("TrackLibraryService.GetAllAsync: awaiting scan task (status={Status})", _scanTask.Status);
        return _scanTask;
    }

    private async Task<IReadOnlyList<Track>> ScanBucketAsync(CancellationToken ct)
    {
        var buckets = storageOptions.Value.Buckets;
        var tracks = new List<Track>();

        logger.LogInformation("ScanBucketAsync: starting, bucket={Bucket}", buckets.Audio);

        try
        {
            var index = 0;
            await foreach (var key in objectStore.ListKeysAsync(buckets.Audio, "local/", ct))
            {
                logger.LogDebug("ScanBucketAsync: key[{Index}]={Key}", index++, key);

                if (!key.EndsWith(".flac", StringComparison.OrdinalIgnoreCase)) continue;

                var fileName = Path.GetFileNameWithoutExtension(key["local/".Length..]);
                var trackId = fileName.ToLowerInvariant();

                var thumbMissing = !await objectStore.ExistsAsync(buckets.Covers, $"local/{trackId}_thumb.webp", ct);
                var largeMissing = !await objectStore.ExistsAsync(buckets.Covers, $"local/{trackId}_large.webp", ct);
                if (thumbMissing || largeMissing)
                    await TryUploadCoverAsync(buckets, trackId, key, ct);

                tracks.Add(new Track(
                    Id: trackId,
                    Title: PrettyTitle(fileName),
                    Artist: "SyncAudio",
                    Album: "Local Library",
                    AudioUrl: $"/track/{Uri.EscapeDataString(trackId)}/audio-url",
                    Duration: TimeSpan.Zero,
                    Palette: Palettes[StableIndex(trackId, Palettes.Length)],
                    Lossless: true,
                    Spatial: ContainsSpatialHint(fileName),
                    CoverUrl: $"/cover/{Uri.EscapeDataString(trackId)}?size=full",
                    Source: TrackSource.Local,
                    RemoteSourceUrl: null));
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ScanBucketAsync: failed to enumerate bucket {Bucket}", buckets.Audio);
            throw;
        }

        logger.LogInformation("ScanBucketAsync: complete, {Count} tracks in bucket {Bucket}", tracks.Count, buckets.Audio);
        return tracks;
    }

    private async Task TryUploadCoverAsync(BucketNames buckets, string trackId, string audioKey, CancellationToken ct)
    {
        var ext = Path.GetExtension(audioKey);
        var tempPath = Path.Combine(Path.GetTempPath(), $"syncaudio-cover-{trackId}{ext}");
        try
        {
            await using (var src = await objectStore.OpenReadAsync(buckets.Audio, audioKey, ct))
            await using (var dst = File.Create(tempPath))
                await src.CopyToAsync(dst, ct);

            using var tagFile = TagLib.File.Create(tempPath);
            var pic = tagFile.Tag.Pictures.FirstOrDefault();
            if (pic is null) return;

            var data = pic.Data.Data;
            var sourceMime = !string.IsNullOrEmpty(pic.MimeType) ? pic.MimeType : CoverMime.Detect(data);

            if (!await objectStore.ExistsAsync(buckets.Covers, $"local/{trackId}_thumb.webp", ct))
            {
                using var thumb = CoverProcessor.MakeThumb(data);
                await objectStore.PutAsync(buckets.Covers, $"local/{trackId}_thumb.webp", thumb, "image/webp", ct);
            }

            if (!await objectStore.ExistsAsync(buckets.Covers, $"local/{trackId}_orig", ct))
            {
                using var full = new MemoryStream(data, writable: false);
                await objectStore.PutAsync(buckets.Covers, $"local/{trackId}_orig", full, sourceMime, ct);
            }

            if (!await objectStore.ExistsAsync(buckets.Covers, $"local/{trackId}_large.webp", ct))
            {
                using var large = CoverProcessor.MakeLarge(data);
                await objectStore.PutAsync(buckets.Covers, $"local/{trackId}_large.webp", large, "image/webp", ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Cover upload failed for track {TrackId}", trackId);
        }
        finally
        {
            try { File.Delete(tempPath); } catch { /* best-effort */ }
        }
    }

    private static string PrettyTitle(string raw)
    {
        var spaced = raw.Replace('_', ' ').Replace('-', ' ').Trim();
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(spaced.ToLowerInvariant());
    }

    private static bool ContainsSpatialHint(string name)
    {
        var lower = name.ToLowerInvariant();
        return lower.Contains("quadro") || lower.Contains("5.1") || lower.Contains("7.1") || lower.Contains("atmos");
    }

    private static int StableIndex(string key, int modulo)
    {
        unchecked
        {
            var hash = key.Aggregate(23, (current, c) => current * 31 + c);
            return (hash & 0x7fffffff) % modulo;
        }
    }
}
