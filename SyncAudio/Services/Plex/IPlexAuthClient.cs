namespace SyncAudio.Services.Plex;

public interface IPlexAuthClient
{
    Task<PlexPin> CreatePinAsync(CancellationToken ct = default);
    Task<PlexPin> CheckPinAsync(long pinId, CancellationToken ct = default);
    Task<string?> GetUsernameAsync(string token, CancellationToken ct = default);
}
