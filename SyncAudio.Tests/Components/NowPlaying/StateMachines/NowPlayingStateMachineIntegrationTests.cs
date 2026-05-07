using SyncAudio.Client.Components.Pages.NowPlaying;
using SyncAudio.Client.Components.Pages.NowPlaying.StateMachines;
using Xunit;

namespace SyncAudio.Tests.Components.NowPlaying.StateMachines;

/// <summary>
/// Verifies the derived boolean getters on <see cref="NowPlayingState"/> reflect the
/// underlying <see cref="LocalDeviceStateMachine"/>, and that the <c>IsWaitingForPeers</c>
/// auto-clear hook fires on entry to Playing.
/// </summary>
public class NowPlayingStateMachineIntegrationTests
{
    [Fact]
    public void Initial_state_all_booleans_false()
    {
        var s = new NowPlayingState();
        Assert.False(s.Machine.IsJoined);
        Assert.False(s.Machine.IsSplitting);
        Assert.False(s.Machine.IsBuffering);
        Assert.False(s.Machine.IsReady);
        Assert.False(s.Machine.IsPlaying);
    }

    [Fact]
    public void After_Joined_only_IsJoined_is_true()
    {
        var s = new NowPlayingState();
        s.Machine.Fire(LocalDeviceTrigger.Joined);

        Assert.True(s.Machine.IsJoined);
        Assert.False(s.Machine.IsSplitting);
        Assert.False(s.Machine.IsBuffering);
        Assert.False(s.Machine.IsReady);
        Assert.False(s.Machine.IsPlaying);
    }

    [Fact]
    public void After_TrackSelected_IsSplitting_only()
    {
        var s = new NowPlayingState();
        s.Machine.Fire(LocalDeviceTrigger.Joined);
        s.Machine.Fire(LocalDeviceTrigger.TrackSelected);

        Assert.True(s.Machine.IsJoined);
        Assert.True(s.Machine.IsSplitting);
        Assert.False(s.Machine.IsBuffering);
        Assert.False(s.Machine.IsReady);
        Assert.False(s.Machine.IsPlaying);
    }

    [Fact]
    public void After_SplitDone_IsBuffering_only()
    {
        var s = new NowPlayingState();
        s.Machine.Fire(LocalDeviceTrigger.Joined);
        s.Machine.Fire(LocalDeviceTrigger.TrackSelected);
        s.Machine.Fire(LocalDeviceTrigger.SplitDone);

        Assert.True(s.Machine.IsJoined);
        Assert.False(s.Machine.IsSplitting);
        Assert.True(s.Machine.IsBuffering);
        Assert.False(s.Machine.IsReady);
        Assert.False(s.Machine.IsPlaying);
    }

    [Fact]
    public void After_BufferReady_IsReady_only()
    {
        var s = new NowPlayingState();
        s.Machine.Fire(LocalDeviceTrigger.Joined);
        s.Machine.Fire(LocalDeviceTrigger.TrackSelected);
        s.Machine.Fire(LocalDeviceTrigger.SplitDone);
        s.Machine.Fire(LocalDeviceTrigger.BufferReady);

        Assert.True(s.Machine.IsJoined);
        Assert.False(s.Machine.IsSplitting);
        Assert.False(s.Machine.IsBuffering);
        Assert.True(s.Machine.IsReady);
        Assert.False(s.Machine.IsPlaying);
    }

    [Fact]
    public void After_PlayStarted_IsPlaying_AND_IsReady_true()
    {
        // IsReady deliberately stays true during Playing — the IsBuffering computed
        // depends on this so the spinner doesn't flash during normal playback.
        var s = new NowPlayingState();
        s.Machine.Fire(LocalDeviceTrigger.Joined);
        s.Machine.Fire(LocalDeviceTrigger.TrackSelected);
        s.Machine.Fire(LocalDeviceTrigger.SplitDone);
        s.Machine.Fire(LocalDeviceTrigger.BufferReady);
        s.Machine.Fire(LocalDeviceTrigger.PlayStarted);

        Assert.True(s.Machine.IsJoined);
        Assert.False(s.Machine.IsSplitting);
        Assert.False(s.Machine.IsBuffering);
        Assert.True(s.Machine.IsReady);
        Assert.True(s.Machine.IsPlaying);
    }

    [Fact]
    public void Entry_to_Playing_clears_IsWaitingForPeers()
    {
        var s = new NowPlayingState
        {
            IsWaitingForPeers = true,
        };
        s.Machine.Fire(LocalDeviceTrigger.Joined);
        s.Machine.Fire(LocalDeviceTrigger.TrackSelected);
        s.Machine.Fire(LocalDeviceTrigger.SplitDone);
        s.Machine.Fire(LocalDeviceTrigger.BufferReady);

        Assert.True(s.IsWaitingForPeers); // not yet playing

        s.Machine.Fire(LocalDeviceTrigger.PlayStarted);
        Assert.False(s.IsWaitingForPeers);
    }

    [Fact]
    public void CanTransport_requires_CurrentTrack_and_active_state()
    {
        var s = new NowPlayingState();
        s.Machine.Fire(LocalDeviceTrigger.Joined);

        Assert.False(s.CanTransport); // no track yet

        s.CurrentTrack = new SyncAudio.Core.Models.Track(
            Id: "t1", Title: "T", Artist: "A", Album: "Alb", AudioUrl: "x.mp3",
            Duration: TimeSpan.FromSeconds(60), Palette: ["#000"], Lossless: false,
            Spatial: false, CoverUrl: null, Source: SyncAudio.Core.Models.TrackSource.Local,
            RemoteSourceUrl: null);
        // Joined+CurrentTrack but state == Idle (no transition into Splitting/Buffering/Ready/Playing yet)
        Assert.False(s.CanTransport);

        s.Machine.Fire(LocalDeviceTrigger.TrackSelected);
        Assert.True(s.CanTransport); // Splitting is enough
    }
}
