namespace SyncAudio.Core.Services.CoverArt;

public interface ICoverArtService
{
    /// <summary>
    /// Extracts cover art from <paramref name="filePath"/>, persists a lossy WebP thumbnail
    /// and the original picture bytes verbatim (no transcoding) for the full size to object
    /// storage. Called synchronously from the track-library scan.
    /// </summary>
    bool PrimeFromFile(string trackId, string filePath);

    /// <summary>
    /// Returns the cover bytes plus their MIME type for the requested size, or null if no
    /// cover was found. <paramref name="size"/> is "thumb" (max 256 px lossy WebP) or "full"
    /// (the original embedded picture, byte-for-byte). Results are memory-cached with a
    /// sliding 30-minute expiry.
    /// </summary>
    Task<(byte[] Data, string Mime)?> GetCoverAsync(string trackId, string size, CancellationToken ct = default);
}
