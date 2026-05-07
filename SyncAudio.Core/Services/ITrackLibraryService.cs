using SyncAudio.Core.Models;

namespace SyncAudio.Core.Services;

public interface ITrackLibraryService
{
    Task<IReadOnlyList<Track>> GetAllAsync();
}
