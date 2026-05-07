namespace SyncAudio.Core.Services.Plex;

public interface IPlexSessionStore
{
    PlexSession? Get();
    void Set(PlexSession session);
    void Clear();
}
