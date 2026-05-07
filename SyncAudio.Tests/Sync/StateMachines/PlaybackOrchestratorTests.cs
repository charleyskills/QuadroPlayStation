using Microsoft.Extensions.Logging.Abstractions;
using SyncAudio.Core.Services.Sync.StateMachines;
using Xunit;

namespace SyncAudio.Tests.Sync.StateMachines;

public class PlaybackOrchestratorTests
{
    private const long FixedNowMs = 1_700_000_000_000L;
    private const long MinLeadMs = 5_000L;

    private static PlaybackOrchestrator NewOrchestrator() =>
        new(NullLogger<PlaybackOrchestrator>.Instance, () => FixedNowMs);

    [Fact]
    public void Join_emits_MemberCount_and_PeersStateUpdated()
    {
        var o = NewOrchestrator();
        var effects = o.OnJoin("conn1", "demo", "PC");

        var memberCount = Assert.Single(effects.OfType<GroupBroadcast>(), e => e.Method == "MemberCount");
        Assert.Equal(1, memberCount.Args[0]);

        var peersState = Assert.Single(effects.OfType<GroupBroadcast>(), e => e.Method == "PeersStateUpdated");
        Assert.Equal("demo", peersState.Group);
    }

    [Fact]
    public void RequestPlay_with_solo_ready_caller_emits_ScheduledPlay()
    {
        var o = NewOrchestrator();
        o.OnJoin("conn1", "demo", "PC");
        o.OnReportPhase("conn1", "demo", "ready", 1.0);

        var effects = o.OnRequestPlay("conn1", "demo", "track-7", leadSeconds: 3.0);

        var play = Assert.Single(effects.OfType<GroupBroadcast>(), e => e.Method == "ScheduledPlay");
        Assert.Equal("demo", play.Group);
        Assert.Equal(FixedNowMs + MinLeadMs, play.Args[0]);
        Assert.Equal("track-7", play.Args[1]);

        // Caller is solo, so no WaitingForPeers is emitted.
        Assert.DoesNotContain(effects, e => e is ClientSend cs && cs.Method == "WaitingForPeers");
    }

    [Fact]
    public void RequestPlay_with_buffering_peer_emits_TrackSelected_and_WaitingForPeers_only()
    {
        var o = NewOrchestrator();
        o.OnJoin("conn1", "demo", "PC");
        o.OnJoin("conn2", "demo", "Phone");
        o.OnReportPhase("conn1", "demo", "ready", 1.0);
        o.OnReportPhase("conn2", "demo", "buffering", 0.4);

        var effects = o.OnRequestPlay("conn1", "demo", "track-7", leadSeconds: 3.0);

        Assert.DoesNotContain(effects, e => e is GroupBroadcast gb && gb.Method == "ScheduledPlay");
        Assert.Single(effects.OfType<OthersInGroupBroadcast>(), e => e.Method == "TrackSelected");
        Assert.Single(effects.OfType<ClientSend>(), e => e.Method == "WaitingForPeers" && e.ConnectionId == "conn1");
    }

    [Fact]
    public void Slow_peer_finishing_buffer_emits_deferred_ScheduledPlay()
    {
        var o = NewOrchestrator();
        o.OnJoin("conn1", "demo", "PC");
        o.OnJoin("conn2", "demo", "Phone");
        o.OnReportPhase("conn1", "demo", "ready", 1.0);
        o.OnReportPhase("conn2", "demo", "buffering", 0.4);
        o.OnRequestPlay("conn1", "demo", "track-7", leadSeconds: 3.0);

        // Slow peer finishes.
        var effects = o.OnReportProgress("conn2", "demo", 1.0, FixedNowMs);

        var play = Assert.Single(effects.OfType<GroupBroadcast>(), e => e.Method == "ScheduledPlay");
        Assert.Equal("track-7", play.Args[1]);
    }

    [Fact]
    public void OnDisconnect_of_last_not_ready_peer_emits_ScheduledPlay()
    {
        var o = NewOrchestrator();
        o.OnJoin("conn1", "demo", "PC");
        o.OnJoin("conn2", "demo", "Phone");
        o.OnReportPhase("conn1", "demo", "ready", 1.0);
        o.OnReportPhase("conn2", "demo", "buffering", 0.2);
        o.OnRequestPlay("conn1", "demo", "track-7", leadSeconds: 3.0);

        var effects = o.OnDisconnect("conn2");

        Assert.Single(effects.OfType<GroupBroadcast>(), e => e.Method == "ScheduledPlay");
    }

    [Fact]
    public void RequestStop_emits_StopPlay_and_resets_group()
    {
        var o = NewOrchestrator();
        o.OnJoin("conn1", "demo", "PC");
        o.OnReportPhase("conn1", "demo", "ready", 1.0);
        o.OnRequestPlay("conn1", "demo", "track-7", leadSeconds: 3.0);

        var effects = o.OnRequestStop("conn1", "demo");

        Assert.Single(effects.OfType<GroupBroadcast>(), e => e.Method == "StopPlay");
    }

    [Fact]
    public void ReportProgress_re_anchors_after_drop()
    {
        var o = NewOrchestrator();
        o.OnJoin("conn1", "demo", "PC");

        // Climb to 0.5
        o.OnReportProgress("conn1", "demo", 0.5, FixedNowMs);
        // Drop to 0.05 (new track started — re-anchor)
        var effects = o.OnReportProgress("conn1", "demo", 0.05, FixedNowMs + 100);

        // Just verifying it doesn't throw and emits PeersStateUpdated.
        Assert.Contains(effects, e => e is GroupBroadcast gb && gb.Method == "PeersStateUpdated");
    }

    [Fact]
    public void ReportPhase_with_unknown_string_does_not_throw()
    {
        var o = NewOrchestrator();
        o.OnJoin("conn1", "demo", "PC");

        var effects = o.OnReportPhase("conn1", "demo", "garbage-phase", 0.5);

        Assert.Contains(effects, e => e is GroupBroadcast gb && gb.Method == "PeersStateUpdated");
    }

    [Fact]
    public void PeersStateUpdated_payload_uses_wire_phase_strings()
    {
        var o = NewOrchestrator();
        o.OnJoin("conn1", "demo", "PC");
        o.OnReportPhase("conn1", "demo", "ready", 1.0);
        var effects = o.OnReportPhase("conn1", "demo", "ready", 1.0);

        var peersUpdate = (GroupBroadcast)effects.First(e => e is GroupBroadcast gb && gb.Method == "PeersStateUpdated");
        // The payload is List<object> of anonymous-typed items; we just verify the
        // serialized JSON would contain "ready" by reflecting on the first peer.
        var peersList = Assert.IsType<List<object>>(peersUpdate.Args[0]);
        var peer = peersList[0]!;
        var phaseProp = peer.GetType().GetProperty("phase");
        Assert.NotNull(phaseProp);
        Assert.Equal("ready", phaseProp!.GetValue(peer));
    }

    [Fact]
    public void BothPlaying_thenDevice1ChangesTrack_emitsScheduledPlayForNewTrack()
    {
        var o = NewOrchestrator();
        o.OnJoin("conn1", "demo", "PC");
        o.OnJoin("conn2", "demo", "Phone");

        // Get both devices to Playing via the normal ready → RequestPlay → ScheduledPlay cycle.
        o.OnReportPhase("conn1", "demo", "ready", 1.0);
        o.OnReportPhase("conn2", "demo", "buffering", 0.5);
        o.OnRequestPlay("conn1", "demo", "track-A", leadSeconds: 3.0);
        o.OnReportProgress("conn2", "demo", 1.0, FixedNowMs); // conn2 ready → ScheduledPlay(A)
        o.OnReportPhase("conn1", "demo", "playing", 1.0);
        o.OnReportPhase("conn2", "demo", "playing", 1.0);

        // Device1 starts loading track-B while both are playing.
        o.OnReportPhase("conn1", "demo", "splitting", 0.0);
        o.OnReportPhase("conn1", "demo", "buffering", 0.0);
        o.OnReportProgress("conn1", "demo", 1.0, FixedNowMs); // conn1 ready

        var requestEffects = o.OnRequestPlay("conn1", "demo", "track-B", leadSeconds: 3.0);
        Assert.Single(requestEffects.OfType<OthersInGroupBroadcast>(), e => e.Method == "TrackSelected");
        Assert.DoesNotContain(requestEffects, e => e is GroupBroadcast gb && gb.Method == "ScheduledPlay");

        // Device2 mirrors and completes buffering.
        o.OnReportPhase("conn2", "demo", "splitting", 0.0);
        o.OnReportPhase("conn2", "demo", "buffering", 0.0);
        var finalEffects = o.OnReportProgress("conn2", "demo", 1.0, FixedNowMs);

        var play = Assert.Single(finalEffects.OfType<GroupBroadcast>(), e => e.Method == "ScheduledPlay");
        Assert.Equal("track-B", play.Args[1]);
    }

    [Fact]
    public void Solo_RequestPlay_after_natural_track_end_emits_ScheduledPlay()
    {
        var o = NewOrchestrator();
        o.OnJoin("conn1", "demo", "PC");
        o.OnReportPhase("conn1", "demo", "ready", 1.0);
        o.OnRequestPlay("conn1", "demo", "track-A", leadSeconds: 3.0);
        o.OnReportPhase("conn1", "demo", "playing", 1.0);

        // Track A ends naturally — client sends no phase update to hub.
        // Client now loads track B (splitting → buffering → decode complete).
        o.OnReportPhase("conn1", "demo", "splitting", 0.0);
        o.OnReportPhase("conn1", "demo", "buffering", 0.0);
        o.OnReportProgress("conn1", "demo", 1.0, FixedNowMs);

        var effects = o.OnRequestPlay("conn1", "demo", "track-B", leadSeconds: 3.0);

        var play = Assert.Single(effects.OfType<GroupBroadcast>(), e => e.Method == "ScheduledPlay");
        Assert.Equal("track-B", play.Args[1]);
        Assert.DoesNotContain(effects, e => e is ClientSend cs && cs.Method == "WaitingForPeers");
    }

    [Fact]
    public void Disconnect_removes_member_and_emits_PeersStateUpdated()
    {
        var o = NewOrchestrator();
        o.OnJoin("conn1", "demo", "PC");
        o.OnJoin("conn2", "demo", "Phone");

        var effects = o.OnDisconnect("conn2");

        var peersUpdate = (GroupBroadcast)effects.First(e => e is GroupBroadcast gb && gb.Method == "PeersStateUpdated");
        var peersList = Assert.IsType<List<object>>(peersUpdate.Args[0]);
        Assert.Single(peersList);
    }
}
