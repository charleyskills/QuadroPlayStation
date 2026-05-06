using SyncAudio.Services.Sync.StateMachines;
using Xunit;

namespace SyncAudio.Tests.Sync.StateMachines;

public class GroupStateMachineTests
{
    private const long FixedNowMs = 1_700_000_000_000L;
    private const long MinLeadMs = 5_000L;

    private static GroupStateMachine NewGroup(Dictionary<string, DeviceState>? deviceStates = null) =>
        new(
            "demo",
            connId => deviceStates is not null && deviceStates.TryGetValue(connId, out var s) ? s : DeviceState.Idle,
            () => FixedNowMs);

    [Fact]
    public void Initial_state_is_Idle()
    {
        var g = NewGroup();
        Assert.Equal(GroupState.Idle, g.State);
        Assert.Empty(g.Members);
    }

    [Fact]
    public void Solo_ready_caller_fires_immediately_emitting_ScheduledPlay()
    {
        var dev = new Dictionary<string, DeviceState> { ["conn1"] = DeviceState.Ready };
        var g = NewGroup(dev);
        g.Members.Add("conn1");

        var effects = new List<HubEffect>();
        g.EffectSink = effects.Add;

        g.PendingTrackId = "track-7";
        g.RequestorConnectionId = "conn1";
        g.PendingLeadSeconds = 3.0;
        g.Fire(GroupTrigger.PlayRequested);

        Assert.Equal(GroupState.Playing, g.State);
        var effect = Assert.Single(effects);
        var broadcast = Assert.IsType<GroupBroadcast>(effect);
        Assert.Equal("demo", broadcast.Group);
        Assert.Equal("ScheduledPlay", broadcast.Method);
        Assert.Equal(FixedNowMs + MinLeadMs, broadcast.Args[0]);
        Assert.Equal("track-7", broadcast.Args[1]);
    }

    [Fact]
    public void PendingPlay_with_buffering_peer_does_not_fire_yet()
    {
        var dev = new Dictionary<string, DeviceState>
        {
            ["conn1"] = DeviceState.Ready,
            ["conn2"] = DeviceState.Buffering,
        };
        var g = NewGroup(dev);
        g.Members.Add("conn1");
        g.Members.Add("conn2");

        var effects = new List<HubEffect>();
        g.EffectSink = effects.Add;
        g.PendingTrackId = "track-7";
        g.PendingLeadSeconds = 3.0;
        g.Fire(GroupTrigger.PlayRequested);

        Assert.Equal(GroupState.PendingPlay, g.State);
        Assert.Empty(effects);
    }

    [Fact]
    public void Late_peer_ready_advances_to_Playing()
    {
        var dev = new Dictionary<string, DeviceState>
        {
            ["conn1"] = DeviceState.Ready,
            ["conn2"] = DeviceState.Buffering,
        };
        var g = NewGroup(dev);
        g.Members.Add("conn1");
        g.Members.Add("conn2");

        g.PendingTrackId = "track-7";
        g.PendingLeadSeconds = 3.0;
        g.Fire(GroupTrigger.PlayRequested);
        Assert.Equal(GroupState.PendingPlay, g.State);

        // Slow peer finishes buffering.
        dev["conn2"] = DeviceState.Ready;

        var effects = new List<HubEffect>();
        g.EffectSink = effects.Add;
        g.TryAdvanceIfAllReady();

        Assert.Equal(GroupState.Playing, g.State);
        Assert.Single(effects);
    }

    [Fact]
    public void Member_joining_during_PendingPlay_keeps_pending()
    {
        var dev = new Dictionary<string, DeviceState>
        {
            ["conn1"] = DeviceState.Ready,
        };
        var g = NewGroup(dev);
        g.Members.Add("conn1");

        // Solo ready caller — would normally fire instantly. Set pending fields, but
        // first add a Buffering peer to stop the fire.
        dev["conn2"] = DeviceState.Buffering;
        g.Members.Add("conn2");

        g.PendingTrackId = "track-7";
        g.PendingLeadSeconds = 3.0;
        var effects = new List<HubEffect>();
        g.EffectSink = effects.Add;
        g.Fire(GroupTrigger.PlayRequested);

        Assert.Equal(GroupState.PendingPlay, g.State);
        Assert.Empty(effects);

        // Another member joins (Idle). Pending stays.
        dev["conn3"] = DeviceState.Idle;
        g.Members.Add("conn3");
        g.Fire(GroupTrigger.MemberJoined);
        Assert.Equal(GroupState.PendingPlay, g.State);
    }

    [Fact]
    public void Last_not_ready_member_leaves_during_PendingPlay_unblocks_fire()
    {
        var dev = new Dictionary<string, DeviceState>
        {
            ["conn1"] = DeviceState.Ready,
            ["conn2"] = DeviceState.Buffering,
        };
        var g = NewGroup(dev);
        g.Members.Add("conn1");
        g.Members.Add("conn2");

        g.PendingTrackId = "track-7";
        g.PendingLeadSeconds = 3.0;
        g.Fire(GroupTrigger.PlayRequested);
        Assert.Equal(GroupState.PendingPlay, g.State);

        // The slow peer disconnects.
        g.Members.Remove("conn2");
        dev.Remove("conn2");
        var effects = new List<HubEffect>();
        g.EffectSink = effects.Add;
        g.Fire(GroupTrigger.MemberLeft);
        g.TryAdvanceIfAllReady();

        Assert.Equal(GroupState.Playing, g.State);
        Assert.Single(effects);
    }

    [Fact]
    public void Stop_from_PendingPlay_returns_to_Idle_and_clears_fields()
    {
        var dev = new Dictionary<string, DeviceState>
        {
            ["conn1"] = DeviceState.Ready,
            ["conn2"] = DeviceState.Buffering,
        };
        var g = NewGroup(dev);
        g.Members.Add("conn1");
        g.Members.Add("conn2");

        g.PendingTrackId = "track-7";
        g.RequestorConnectionId = "conn1";
        g.PendingLeadSeconds = 3.0;
        g.Fire(GroupTrigger.PlayRequested);
        Assert.Equal(GroupState.PendingPlay, g.State);

        g.Fire(GroupTrigger.Stop);
        Assert.Equal(GroupState.Idle, g.State);
        Assert.Null(g.PendingTrackId);
        Assert.Null(g.RequestorConnectionId);
        Assert.Equal(0, g.PendingLeadSeconds);
    }

    [Fact]
    public void TrackChanged_while_Playing_returns_to_PendingPlay()
    {
        var dev = new Dictionary<string, DeviceState>
        {
            ["conn1"] = DeviceState.Ready,
        };
        var g = NewGroup(dev);
        g.Members.Add("conn1");

        g.PendingTrackId = "track-A";
        g.PendingLeadSeconds = 3.0;
        g.Fire(GroupTrigger.PlayRequested);
        Assert.Equal(GroupState.Playing, g.State);

        // New track requested — caller updates fields then fires PlayRequested.
        // Per the orchestrator, peers (none here) get reset to Buffering first.
        g.PendingTrackId = "track-B";
        g.PendingLeadSeconds = 3.0;
        g.Fire(GroupTrigger.PlayRequested);
        // Solo+ready immediately advances to Playing again.
        Assert.Equal(GroupState.Playing, g.State);
    }

    [Fact]
    public void Stale_Ready_for_old_track_does_not_satisfy_AllMembersReady()
    {
        var dev = new Dictionary<string, DeviceState>
        {
            ["conn1"] = DeviceState.Ready,
            ["conn2"] = DeviceState.Ready, // stale Ready left over from previous track
        };
        var nowPlaying = new Dictionary<string, string?>
        {
            ["conn1"] = "track-B",
            ["conn2"] = "track-A", // peer hasn't mirrored the new track yet
        };
        var g = new GroupStateMachine(
            "demo",
            connId => dev.TryGetValue(connId, out var s) ? s : DeviceState.Idle,
            () => FixedNowMs,
            connId => nowPlaying.TryGetValue(connId, out var t) ? t : null);
        g.Members.Add("conn1");
        g.Members.Add("conn2");

        var effects = new List<HubEffect>();
        g.EffectSink = effects.Add;
        g.PendingTrackId = "track-B";
        g.PendingLeadSeconds = 3.0;
        g.Fire(GroupTrigger.PlayRequested);

        // Both are nominally Ready, but conn2's NowPlaying still references the old
        // track — must NOT auto-advance to Playing.
        Assert.Equal(GroupState.PendingPlay, g.State);
        Assert.Empty(effects);

        // Peer mirrors the new track and re-confirms.
        nowPlaying["conn2"] = "track-B";
        g.TryAdvanceIfAllReady();

        Assert.Equal(GroupState.Playing, g.State);
        Assert.Single(effects);
    }

    [Fact]
    public void Lead_seconds_below_min_are_clamped_to_MinLeadMs()
    {
        var dev = new Dictionary<string, DeviceState> { ["conn1"] = DeviceState.Ready };
        var g = NewGroup(dev);
        g.Members.Add("conn1");

        var effects = new List<HubEffect>();
        g.EffectSink = effects.Add;
        g.PendingTrackId = "t";
        g.PendingLeadSeconds = 1.0; // below 5s floor
        g.Fire(GroupTrigger.PlayRequested);

        var b = Assert.IsType<GroupBroadcast>(Assert.Single(effects));
        Assert.Equal(FixedNowMs + MinLeadMs, b.Args[0]);
    }
}
