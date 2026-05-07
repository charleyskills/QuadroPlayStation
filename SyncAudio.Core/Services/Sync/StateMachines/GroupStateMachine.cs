using Stateless;

namespace SyncAudio.Core.Services.Sync.StateMachines;

/// <summary>
/// Per-group state machine. Owns membership and pending-play coordination.
/// Replaces the implicit <c>PendingPlays_</c> dictionary + <c>TryTriggerPendingPlay</c>
/// method that lived on the old SyncHub.
///
/// Effects (ScheduledPlay, StopPlay) are emitted via <see cref="EffectSink"/>, set by
/// the orchestrator before firing a trigger and cleared after — keeps the machine free
/// of <c>IHubContext</c> dependencies and trivially testable.
/// </summary>
public sealed class GroupStateMachine
{
    private readonly StateMachine<GroupState, GroupTrigger> _sm;
    private readonly Func<string, DeviceState> _lookupDeviceState;
    private readonly Func<string, string?>? _lookupNowPlayingTrackId;
    private readonly Func<long> _nowMs;

    public string GroupName { get; }
    public GroupState State => _sm.State;

    /// <summary>Connection IDs of all current group members.</summary>
    public HashSet<string> Members { get; } = new();

    /// <summary>Track ID for the pending play (only meaningful while <c>State == PendingPlay</c>).</summary>
    public string? PendingTrackId { get; set; }

    /// <summary>Connection ID of the device that issued the RequestPlay.</summary>
    public string? RequestorConnectionId { get; set; }

    /// <summary>Lead seconds requested by the caller (clamped to <see cref="AdaptiveLeadCalculator.MinLeadMs"/>).</summary>
    public double PendingLeadSeconds { get; set; }

    /// <summary>Set by the orchestrator before firing a trigger; the machine appends effects here.</summary>
    public Action<HubEffect>? EffectSink { get; set; }

    public event Action<GroupState, GroupState, GroupTrigger>? Transitioned;

    public GroupStateMachine(
        string groupName,
        Func<string, DeviceState> lookupDeviceState,
        Func<long> nowMs,
        Func<string, string?>? lookupNowPlayingTrackId = null)
    {
        GroupName = groupName;
        _lookupDeviceState = lookupDeviceState;
        _lookupNowPlayingTrackId = lookupNowPlayingTrackId;
        _nowMs = nowMs;
        _sm = new StateMachine<GroupState, GroupTrigger>(GroupState.Idle, FiringMode.Queued);
        Configure();
        _sm.OnTransitioned(t => Transitioned?.Invoke(t.Source, t.Destination, t.Trigger));
    }

    private void Configure()
    {
        _sm.Configure(GroupState.Idle)
            .OnEntry(ClearPending)
            .PermitReentry(GroupTrigger.MemberJoined)
            .PermitReentry(GroupTrigger.MemberLeft)
            .PermitReentry(GroupTrigger.Stop)
            .PermitReentry(GroupTrigger.AllMembersReady) // no-op if no pending play
            .Permit(GroupTrigger.PlayRequested, GroupState.PendingPlay);

        _sm.Configure(GroupState.PendingPlay)
            .OnEntry(OnEnterPendingPlay)
            .PermitReentry(GroupTrigger.MemberJoined)
            .PermitReentry(GroupTrigger.MemberLeft)
            .PermitReentry(GroupTrigger.PlayRequested) // same or new track — caller updated fields before firing
            .PermitReentry(GroupTrigger.TrackChanged)
            .PermitIf(GroupTrigger.AllMembersReady, GroupState.Playing, AllMembersAreReady)
            .Permit(GroupTrigger.Stop, GroupState.Idle);

        _sm.Configure(GroupState.Playing)
            .OnEntry(OnEnterPlaying)
            .PermitReentry(GroupTrigger.MemberJoined)
            .PermitReentry(GroupTrigger.MemberLeft)
            .PermitReentry(GroupTrigger.AllMembersReady) // no-op
            .Permit(GroupTrigger.PlayRequested, GroupState.PendingPlay)
            .Permit(GroupTrigger.TrackChanged, GroupState.PendingPlay)
            .Permit(GroupTrigger.Stop, GroupState.Idle);
    }

    private bool AllMembersAreReady()
    {
        if (Members.Count == 0) return false;
        foreach (var id in Members)
        {
            if (_lookupDeviceState(id) != DeviceState.Ready) return false;
            // Track-aware freshness guard: if a member's reported NowPlaying track is
            // known and explicitly differs from PendingTrackId, treat them as not ready.
            // A stale Ready from a previous track must not satisfy AllMembersReady for
            // the new request — otherwise the caller would broadcast ScheduledPlay before
            // peers have actually buffered the new track. Null lookup or null reported
            // track is lenient (skip the check) so members that haven't reported a
            // NowPlaying yet (e.g. fresh joins) aren't blocked indefinitely.
            if (PendingTrackId is null) continue;
            var npTrack = _lookupNowPlayingTrackId?.Invoke(id);
            if (npTrack is not null && npTrack != PendingTrackId) return false;
        }
        return true;
    }

    private void OnEnterPendingPlay()
    {
        // Solo-ready-caller short-circuit: if the only member is already Ready,
        // immediately advance to Playing.
        if (_sm.CanFire(GroupTrigger.AllMembersReady))
            _sm.Fire(GroupTrigger.AllMembersReady);
    }

    private void OnEnterPlaying()
    {
        if (PendingTrackId is null) return;
        var lead = Math.Max(AdaptiveLeadCalculator.MinLeadMs, (long)(PendingLeadSeconds * 1000));
        var fireAt = _nowMs() + lead;
        EffectSink?.Invoke(new GroupBroadcast(
            GroupName,
            "ScheduledPlay",
            new object?[] { fireAt, PendingTrackId }));
        // Clear after firing so a subsequent PlayRequested reads fresh values.
        ClearPending();
    }

    private void ClearPending()
    {
        PendingTrackId = null;
        PendingLeadSeconds = 0;
        RequestorConnectionId = null;
    }

    public void Fire(GroupTrigger trigger) => _sm.Fire(trigger);
    public bool CanFire(GroupTrigger trigger) => _sm.CanFire(trigger);

    /// <summary>Re-evaluate AllMembersReady (called after a device transitions to Ready).</summary>
    public void TryAdvanceIfAllReady()
    {
        if (_sm.State == GroupState.PendingPlay && _sm.CanFire(GroupTrigger.AllMembersReady))
            _sm.Fire(GroupTrigger.AllMembersReady);
    }
}
