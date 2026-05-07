namespace SyncAudio.Client.Components.Pages.NowPlaying;

public sealed record NowPlayingContext(
    NowPlayingState State,
    NowPlayingLogic Logic);
