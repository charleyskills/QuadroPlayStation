using System.Globalization;
using SyncAudio.Models;

namespace SyncAudio.Services;

public sealed class TrackLibraryService(
    IWebHostEnvironment env,
    ICoverArtService coverArt,
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

    private IReadOnlyList<Track>? _cache;
    private readonly Lock _lock = new();

    public IReadOnlyList<Track> GetAll()
    {
        if (_cache is not null) return _cache;
        lock (_lock)
        {
            if (_cache is not null) return _cache;
            _cache = ScanDisk();
            return _cache;
        }
    }

    private IReadOnlyList<Track> ScanDisk()
    {
        var audioDir = Path.Combine(env.WebRootPath, "audio");
        if (!Directory.Exists(audioDir))
        {
            logger.LogWarning("Audio directory not found: {Path}", audioDir);
            return [];
        }

        var files = Directory.EnumerateFiles(audioDir, "*.flac", SearchOption.TopDirectoryOnly)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var tracks = new List<Track>(files.Length);
        foreach (var path in files)
        {
            var fileName = Path.GetFileNameWithoutExtension(path);
            var id = fileName.ToLowerInvariant();
            var hasCover = coverArt.PrimeFromFile(id, path);
            tracks.Add(new Track(
                Id: id,
                Title: PrettyTitle(fileName),
                Artist: "SyncAudio",
                Album: "Local Library",
                AudioUrl: "/audio/" + Path.GetFileName(path),
                Duration: TimeSpan.Zero,
                Palette: Palettes[StableIndex(id, Palettes.Length)],
                Lossless: true,
                Spatial: ContainsSpatialHint(fileName),
                CoverUrl: hasCover ? $"/cover/{Uri.EscapeDataString(id)}" : null
            ));
        }

        logger.LogInformation("Loaded {Count} tracks from {Dir}", tracks.Count, audioDir);
        return tracks;
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
