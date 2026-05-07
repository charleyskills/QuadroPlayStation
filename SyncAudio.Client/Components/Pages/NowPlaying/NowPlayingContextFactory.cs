using Microsoft.Extensions.Caching.Memory;
using SyncAudio.Core.Services;
using SyncAudio.Core.Services;
using SyncAudio.Core.Services.TrackSplit;

namespace SyncAudio.Client.Components.Pages.NowPlaying;

public interface INowPlayingContextFactory
{
    NowPlayingContext Create();
    NowPlayingContext Create(NowPlayingConfiguration configuration);
}

public sealed class NowPlayingContextFactory(
    ITrackLibraryService library,
    ITrackSplitService splitter,
    ILogger<NowPlayingLogic> logger,
    IMemoryCache cache,
    CurrentCoverTracker coverTracker)
    : INowPlayingContextFactory
{
    public NowPlayingContext Create() => Create(NowPlayingConfiguration.Default);

    public NowPlayingContext Create(NowPlayingConfiguration configuration)
    {
        var state = new NowPlayingState
        {
            Configuration = configuration,
            Volume = configuration.DefaultVolume,
            CoverTracker = coverTracker,
        };

        var logic = new NowPlayingLogic(library, splitter, logger, state, cache);

        return new NowPlayingContext(state, logic);
    }
}
