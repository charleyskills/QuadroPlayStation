using SyncAudio.Client.Components.Pages.NowPlaying.StateMachines;
using Xunit;

namespace SyncAudio.Tests.Components.NowPlaying.StateMachines;

public class LocalDeviceStateMachineTests
{
    [Fact]
    public void Initial_state_is_Disconnected()
    {
        var sm = new LocalDeviceStateMachine();
        Assert.Equal(LocalDeviceState.Disconnected, sm.State);
    }

    [Fact]
    public void Disconnected_to_Idle_via_Joined()
    {
        var sm = new LocalDeviceStateMachine();
        sm.Fire(LocalDeviceTrigger.Joined);
        Assert.Equal(LocalDeviceState.Idle, sm.State);
    }

    [Fact]
    public void Idle_to_Splitting_via_TrackSelected()
    {
        var sm = new LocalDeviceStateMachine();
        sm.Fire(LocalDeviceTrigger.Joined);
        sm.Fire(LocalDeviceTrigger.TrackSelected);
        Assert.Equal(LocalDeviceState.Splitting, sm.State);
    }

    [Fact]
    public void Full_local_track_lifecycle()
    {
        var sm = new LocalDeviceStateMachine();
        sm.Fire(LocalDeviceTrigger.Joined);
        Assert.Equal(LocalDeviceState.Idle, sm.State);
        sm.Fire(LocalDeviceTrigger.TrackSelected);
        Assert.Equal(LocalDeviceState.Splitting, sm.State);
        sm.Fire(LocalDeviceTrigger.SplitDone);
        Assert.Equal(LocalDeviceState.Buffering, sm.State);
        sm.Fire(LocalDeviceTrigger.BufferReady);
        Assert.Equal(LocalDeviceState.Ready, sm.State);
        sm.Fire(LocalDeviceTrigger.PlayStarted);
        Assert.Equal(LocalDeviceState.Playing, sm.State);
        sm.Fire(LocalDeviceTrigger.PlaybackEnded);
        Assert.Equal(LocalDeviceState.Ready, sm.State);
    }

    [Fact]
    public void Splitting_to_Idle_via_SplitFailed()
    {
        var sm = new LocalDeviceStateMachine();
        sm.Fire(LocalDeviceTrigger.Joined);
        sm.Fire(LocalDeviceTrigger.TrackSelected);
        sm.Fire(LocalDeviceTrigger.SplitFailed);
        Assert.Equal(LocalDeviceState.Idle, sm.State);
    }

    [Fact]
    public void Buffering_to_Playing_directly_for_HTML5_fallback()
    {
        // HTML5 audio can fire `play` before `canplay` — Buffering → Playing must be permitted.
        var sm = new LocalDeviceStateMachine();
        sm.Fire(LocalDeviceTrigger.Joined);
        sm.Fire(LocalDeviceTrigger.TrackSelected);
        sm.Fire(LocalDeviceTrigger.SplitDone);
        Assert.Equal(LocalDeviceState.Buffering, sm.State);
        sm.Fire(LocalDeviceTrigger.PlayStarted);
        Assert.Equal(LocalDeviceState.Playing, sm.State);
    }

    [Fact]
    public void Track_change_while_Playing_returns_to_Splitting()
    {
        var sm = new LocalDeviceStateMachine();
        sm.Fire(LocalDeviceTrigger.Joined);
        sm.Fire(LocalDeviceTrigger.TrackSelected);
        sm.Fire(LocalDeviceTrigger.SplitDone);
        sm.Fire(LocalDeviceTrigger.BufferReady);
        sm.Fire(LocalDeviceTrigger.PlayStarted);
        Assert.Equal(LocalDeviceState.Playing, sm.State);

        sm.Fire(LocalDeviceTrigger.TrackSelected);
        Assert.Equal(LocalDeviceState.Splitting, sm.State);
    }

    [Fact]
    public void BufferReady_while_Playing_is_idempotent()
    {
        var sm = new LocalDeviceStateMachine();
        sm.Fire(LocalDeviceTrigger.Joined);
        sm.Fire(LocalDeviceTrigger.TrackSelected);
        sm.Fire(LocalDeviceTrigger.SplitDone);
        sm.Fire(LocalDeviceTrigger.BufferReady);
        sm.Fire(LocalDeviceTrigger.PlayStarted);

        sm.Fire(LocalDeviceTrigger.BufferReady); // should not transition out of Playing
        Assert.Equal(LocalDeviceState.Playing, sm.State);
    }

    [Fact]
    public void PlaybackBegan_event_fires_on_entry_to_Playing()
    {
        var sm = new LocalDeviceStateMachine();
        var beganCount = 0;
        sm.PlaybackBegan += () => beganCount++;

        sm.Fire(LocalDeviceTrigger.Joined);
        sm.Fire(LocalDeviceTrigger.TrackSelected);
        sm.Fire(LocalDeviceTrigger.SplitDone);
        sm.Fire(LocalDeviceTrigger.BufferReady);
        sm.Fire(LocalDeviceTrigger.PlayStarted);
        Assert.Equal(1, beganCount);

        sm.Fire(LocalDeviceTrigger.PlayStarted); // reentry: should fire again
        Assert.Equal(2, beganCount);
    }

    [Fact]
    public void Transitioned_event_fires_with_source_dest_trigger()
    {
        var sm = new LocalDeviceStateMachine();
        LocalDeviceState? source = null, dest = null;
        LocalDeviceTrigger? trig = null;
        sm.Transitioned += (s, d, t) => { source = s; dest = d; trig = t; };

        sm.Fire(LocalDeviceTrigger.Joined);

        Assert.Equal(LocalDeviceState.Disconnected, source);
        Assert.Equal(LocalDeviceState.Idle, dest);
        Assert.Equal(LocalDeviceTrigger.Joined, trig);
    }

    [Theory]
    [InlineData(LocalDeviceState.Idle)]
    [InlineData(LocalDeviceState.Splitting)]
    [InlineData(LocalDeviceState.Buffering)]
    [InlineData(LocalDeviceState.Ready)]
    [InlineData(LocalDeviceState.Playing)]
    public void Disconnect_from_any_state_returns_to_Disconnected(LocalDeviceState start)
    {
        var sm = new LocalDeviceStateMachine();
        // Climb to the desired starting state.
        sm.Fire(LocalDeviceTrigger.Joined);
        if (start >= LocalDeviceState.Splitting) sm.Fire(LocalDeviceTrigger.TrackSelected);
        if (start >= LocalDeviceState.Buffering) sm.Fire(LocalDeviceTrigger.SplitDone);
        if (start >= LocalDeviceState.Ready) sm.Fire(LocalDeviceTrigger.BufferReady);
        if (start >= LocalDeviceState.Playing) sm.Fire(LocalDeviceTrigger.PlayStarted);
        Assert.Equal(start, sm.State);

        sm.Fire(LocalDeviceTrigger.Disconnected);
        Assert.Equal(LocalDeviceState.Disconnected, sm.State);
    }

    [Fact]
    public void Cannot_PlayStarted_from_Disconnected_or_Idle()
    {
        var sm = new LocalDeviceStateMachine();
        Assert.False(sm.CanFire(LocalDeviceTrigger.PlayStarted));

        sm.Fire(LocalDeviceTrigger.Joined);
        Assert.False(sm.CanFire(LocalDeviceTrigger.PlayStarted));
    }

    [Fact]
    public void Cannot_BufferReady_from_Idle()
    {
        var sm = new LocalDeviceStateMachine();
        sm.Fire(LocalDeviceTrigger.Joined);
        Assert.False(sm.CanFire(LocalDeviceTrigger.BufferReady));
    }
}
