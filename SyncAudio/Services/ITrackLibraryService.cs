using SyncAudio.Models;

namespace SyncAudio.Services;

public interface ITrackLibraryService
{
    IReadOnlyList<Track> GetAll();
}
