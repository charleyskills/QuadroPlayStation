namespace SyncAudio.Services;

public interface ICoverArtService
{
    bool TryGetCover(string trackId, out byte[] data, out string mime);

    bool PrimeFromFile(string trackId, string filePath);
}
