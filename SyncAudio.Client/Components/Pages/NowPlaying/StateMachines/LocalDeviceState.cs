namespace SyncAudio.Client.Components.Pages.NowPlaying.StateMachines;

/// <summary>
/// Per-Blazor-circuit playback lifecycle state. Replaces the overlapping booleans
/// (<c>IsJoined</c>, <c>IsSplitting</c>, <c>IsBuffering</c>, <c>IsReady</c>, <c>IsPlaying</c>)
/// that previously lived as independent fields on <see cref="NowPlayingState"/>.
/// </summary>
public enum LocalDeviceState
{
    Disconnected,
    Idle,
    Splitting,
    Buffering,
    Ready,
    Playing,
}

public enum LocalDeviceTrigger
{
    Joined,
    TrackSelected,
    SplitDone,
    SplitFailed,
    BufferReady,
    PlayStarted,
    PlaybackEnded,
    Disconnected,
}
