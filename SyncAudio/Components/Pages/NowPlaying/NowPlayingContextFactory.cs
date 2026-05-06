using Microsoft.Extensions.Caching.Memory;
using SyncAudio.Services;
using SyncAudio.Services.TrackSplit;

namespace SyncAudio.Components.Pages.NowPlaying;

public interface INowPlayingContextFactory
{
    NowPlayingContext Create();
    NowPlayingContext Create(NowPlayingConfiguration configuration);
}

public sealed class NowPlayingContextFactory(
    ITrackLibraryService library,
    ITrackSplitService splitter,
    ILogger<NowPlayingLogic> logger,
    IMemoryCache cache)
    : INowPlayingContextFactory
{
    public NowPlayingContext Create() => Create(NowPlayingConfiguration.Default);

    public NowPlayingContext Create(NowPlayingConfiguration configuration)
    {
        var state = new NowPlayingState
        {
            Configuration = configuration,
            Volume = configuration.DefaultVolume,
        };

        var logic = new NowPlayingLogic(library, splitter, logger, state, cache);

        return new NowPlayingContext(state, logic);
    }
}
