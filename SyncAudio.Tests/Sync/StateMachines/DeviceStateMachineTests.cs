using SyncAudio.Services.Sync.StateMachines;
using Xunit;

namespace SyncAudio.Tests.Sync.StateMachines;

public class DeviceStateMachineTests
{
    [Fact]
    public void Initial_state_is_Idle()
    {
        var sm = new DeviceStateMachine("conn1");
        Assert.Equal(DeviceState.Idle, sm.State);
    }

    [Theory]
    [InlineData(DeviceTrigger.BeginSplit, DeviceState.Splitting)]
    [InlineData(DeviceTrigger.BeginBuffer, DeviceState.Buffering)]
    [InlineData(DeviceTrigger.SplitDone, DeviceState.Buffering)]
    [InlineData(DeviceTrigger.BufferReady, DeviceState.Ready)]
    [InlineData(DeviceTrigger.PlayStarted, DeviceState.Playing)]
    public void From_Idle_permits_forward_transitions(DeviceTrigger trigger, DeviceState expected)
    {
        var sm = new DeviceStateMachine("conn1");
        sm.Fire(trigger);
        Assert.Equal(expected, sm.State);
    }

    [Fact]
    public void Splitting_to_Buffering_via_SplitDone()
    {
        var sm = new DeviceStateMachine("conn1");
        sm.Fire(DeviceTrigger.BeginSplit);
        sm.Fire(DeviceTrigger.SplitDone);
        Assert.Equal(DeviceState.Buffering, sm.State);
    }

    [Fact]
    public void Splitting_to_Idle_via_SplitFailed()
    {
        var sm = new DeviceStateMachine("conn1");
        sm.Fire(DeviceTrigger.BeginSplit);
        sm.Fire(DeviceTrigger.SplitFailed);
        Assert.Equal(DeviceState.Idle, sm.State);
    }

    [Fact]
    public void Buffering_to_Ready_via_BufferReady()
    {
        var sm = new DeviceStateMachine("conn1");
        sm.Fire(DeviceTrigger.BeginBuffer);
        sm.Fire(DeviceTrigger.BufferReady);
        Assert.Equal(DeviceState.Ready, sm.State);
    }

    [Fact]
    public void Ready_to_Playing_via_PlayStarted()
    {
        var sm = new DeviceStateMachine("conn1");
        sm.Fire(DeviceTrigger.BeginBuffer);
        sm.Fire(DeviceTrigger.BufferReady);
        sm.Fire(DeviceTrigger.PlayStarted);
        Assert.Equal(DeviceState.Playing, sm.State);
    }

    [Fact]
    public void Playing_to_Idle_via_Stopped()
    {
        var sm = new DeviceStateMachine("conn1");
        sm.Fire(DeviceTrigger.BeginBuffer);
        sm.Fire(DeviceTrigger.BufferReady);
        sm.Fire(DeviceTrigger.PlayStarted);
        sm.Fire(DeviceTrigger.Stopped);
        Assert.Equal(DeviceState.Idle, sm.State);
    }

    [Fact]
    public void Playing_to_Buffering_via_BeginBuffer_for_track_change()
    {
        var sm = new DeviceStateMachine("conn1");
        sm.Fire(DeviceTrigger.BeginBuffer);
        sm.Fire(DeviceTrigger.BufferReady);
        sm.Fire(DeviceTrigger.PlayStarted);
        sm.Fire(DeviceTrigger.BeginBuffer);
        Assert.Equal(DeviceState.Buffering, sm.State);
    }

    [Fact]
    public void Playing_to_Ready_via_BufferReady_when_playback_ends_naturally()
    {
        // Mirrors syncAudio.js _stopSource() reporting "ready" if buffer still hot.
        var sm = new DeviceStateMachine("conn1");
        sm.Fire(DeviceTrigger.BeginBuffer);
        sm.Fire(DeviceTrigger.BufferReady);
        sm.Fire(DeviceTrigger.PlayStarted);
        sm.Fire(DeviceTrigger.BufferReady);
        Assert.Equal(DeviceState.Ready, sm.State);
    }

    [Fact]
    public void Idle_PermitReentry_Stopped_does_not_throw()
    {
        var sm = new DeviceStateMachine("conn1");
        sm.Fire(DeviceTrigger.Stopped);
        Assert.Equal(DeviceState.Idle, sm.State);
    }

    [Fact]
    public void Transition_event_fires_with_source_dest_trigger()
    {
        var sm = new DeviceStateMachine("conn1");
        DeviceState? source = null, dest = null;
        DeviceTrigger? trig = null;
        sm.Transitioned += (s, d, t) => { source = s; dest = d; trig = t; };

        sm.Fire(DeviceTrigger.BeginBuffer);

        Assert.Equal(DeviceState.Idle, source);
        Assert.Equal(DeviceState.Buffering, dest);
        Assert.Equal(DeviceTrigger.BeginBuffer, trig);
    }

    [Fact]
    public void LastProgress_is_settable()
    {
        var sm = new DeviceStateMachine("conn1");
        sm.LastProgress = 0.42;
        Assert.Equal(0.42, sm.LastProgress);
    }

    [Theory]
    [InlineData(DeviceState.Idle, "idle")]
    [InlineData(DeviceState.Splitting, "splitting")]
    [InlineData(DeviceState.Buffering, "buffering")]
    [InlineData(DeviceState.Ready, "ready")]
    [InlineData(DeviceState.Playing, "playing")]
    public void WireStrings_round_trip(DeviceState state, string wire)
    {
        Assert.Equal(wire, state.ToWireString());
        Assert.Equal(state, WireStrings.ParseDeviceWire(wire));
    }

    [Fact]
    public void WireStrings_unknown_falls_through_to_Idle()
    {
        Assert.Equal(DeviceState.Idle, WireStrings.ParseDeviceWire("garbage"));
        Assert.Equal(DeviceState.Idle, WireStrings.ParseDeviceWire(null));
    }
}
