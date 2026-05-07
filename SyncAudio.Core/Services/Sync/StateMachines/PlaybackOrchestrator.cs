using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace SyncAudio.Core.Services.Sync.StateMachines;

internal sealed record ClientInfo(string DeviceName, string GroupName);
internal sealed record ClientNowPlaying(string TrackId, string Title, string Artist, string? CoverUrl);

/// <summary>
/// Owns one <see cref="DeviceStateMachine"/> per connected client and one
/// <see cref="GroupStateMachine"/> per active group. Singleton lifetime — replaces
/// the static dictionaries that previously lived on <c>SyncHub</c>.
/// </summary>
public sealed class PlaybackOrchestrator : IPlaybackOrchestrator
{
    private readonly ILogger<PlaybackOrchestrator> _logger;
    private readonly Func<long> _nowMs;

    private readonly ConcurrentDictionary<string, DeviceStateMachine> _devices = new();
    private readonly ConcurrentDictionary<string, GroupStateMachine> _groups = new();
    private readonly ConcurrentDictionary<string, ProgressSample> _progress = new();
    private readonly ConcurrentDictionary<string, ClientInfo> _clientInfos = new();
    private readonly ConcurrentDictionary<string, ClientNowPlaying> _nowPlayings = new();

    // Single global lock guards all state machine fires + dictionary mutations that
    // span device + group machines. The 2-devices-per-group typical case keeps
    // contention minimal. Effects are captured under the lock; SignalR I/O happens
    // outside (in the hub).
    private readonly Lock _lock = new();

    public PlaybackOrchestrator(ILogger<PlaybackOrchestrator> logger)
        : this(logger, () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
    {
    }

    /// <summary>Test seam — lets tests inject a deterministic clock.</summary>
    internal PlaybackOrchestrator(ILogger<PlaybackOrchestrator> logger, Func<long> nowMs)
    {
        _logger = logger;
        _nowMs = nowMs;
    }

    public IReadOnlyList<HubEffect> OnJoin(string connectionId, string group, string deviceName)
    {
        var effects = new List<HubEffect>();
        lock (_lock)
        {
            _devices.GetOrAdd(connectionId, id => new DeviceStateMachine(id));
            var groupSM = GetOrCreateGroup(group);
            groupSM.Members.Add(connectionId);
            FireGroup(groupSM, GroupTrigger.MemberJoined, effects);
            _clientInfos[connectionId] = new ClientInfo(deviceName, group);

            effects.Add(new GroupBroadcast(group, "MemberCount", [groupSM.Members.Count]));
            effects.Add(BuildPeersStateEffect(group));
        }
        return effects;
    }

    public IReadOnlyList<HubEffect> OnReportProgress(string connectionId, string group, double fraction, long nowMs)
    {
        var effects = new List<HubEffect>();
        var clamped = Math.Clamp(fraction, 0.0, 1.0);

        lock (_lock)
        {
            // Re-anchor: a meaningful drop (>0.05) means the client started a new decode;
            // restart First* so AdaptiveLeadCalculator's speed estimation reflects the
            // current download. Logic preserved verbatim from the previous SyncHub.
            _progress.AddOrUpdate(
                connectionId,
                _ => new ProgressSample(clamped, nowMs, clamped, nowMs),
                (_, existing) =>
                {
                    if (clamped + 0.05 < existing.LastFraction)
                        return new ProgressSample(clamped, nowMs, clamped, nowMs);
                    return existing with { LastFraction = clamped, LastTimestampMs = nowMs };
                });

            if (_devices.TryGetValue(connectionId, out var device))
            {
                device.LastProgress = clamped;
                var trigger = clamped >= 1.0 ? DeviceTrigger.BufferReady : DeviceTrigger.BeginBuffer;
                if (device.CanFire(trigger)) device.Fire(trigger);
            }

            if (_groups.TryGetValue(group, out var groupSM))
            {
                groupSM.EffectSink = effects.Add;
                try { groupSM.TryAdvanceIfAllReady(); }
                finally { groupSM.EffectSink = null; }
            }

            effects.Add(BuildPeersStateEffect(group));
        }
        return effects;
    }

    public IReadOnlyList<HubEffect> OnReportPhase(string connectionId, string group, string wirePhase, double progress)
    {
        var effects = new List<HubEffect>();
        var targetState = WireStrings.ParseDeviceWire(wirePhase, _logger);

        lock (_lock)
        {
            if (_devices.TryGetValue(connectionId, out var device))
            {
                device.LastProgress = Math.Clamp(progress, 0.0, 1.0);
                FireDeviceToTargetState(device, targetState);
            }

            if (_groups.TryGetValue(group, out var groupSM))
            {
                groupSM.EffectSink = effects.Add;
                try { groupSM.TryAdvanceIfAllReady(); }
                finally { groupSM.EffectSink = null; }
            }

            effects.Add(BuildPeersStateEffect(group));
        }
        return effects;
    }

    public IReadOnlyList<HubEffect> OnRequestPlay(string connectionId, string group, string trackId, double leadSeconds)
    {
        var effects = new List<HubEffect>();
        bool firedScheduledPlay;
        int readyCount = 0;
        int totalCount;

        lock (_lock)
        {
            var groupSM = GetOrCreateGroup(group);

            // Reset every NON-CALLER member to Buffering — they must re-confirm readiness
            // for this specific track. Caller keeps its state (already has the buffer).
            foreach (var memberId in groupSM.Members)
            {
                if (memberId == connectionId) continue;
                if (_devices.TryGetValue(memberId, out var member) && member.CanFire(DeviceTrigger.BeginBuffer))
                    member.Fire(DeviceTrigger.BeginBuffer);
            }

            totalCount = groupSM.Members.Count;
            if (_devices.TryGetValue(connectionId, out var caller) && caller.State == DeviceState.Ready)
                readyCount = 1;

            // Set fields BEFORE firing PlayRequested — OnEntry to PendingPlay reads them
            // and the OnEntry to Playing (if solo+ready) emits ScheduledPlay.
            groupSM.PendingTrackId = trackId;
            groupSM.RequestorConnectionId = connectionId;
            groupSM.PendingLeadSeconds = leadSeconds;

            FireGroup(groupSM, GroupTrigger.PlayRequested, effects);
            firedScheduledPlay = groupSM.State == GroupState.Playing;
        }

        // Ask peers to buffer the new track. (Empty broadcast for solo group is harmless.)
        effects.Add(new OthersInGroupBroadcast(group, "TrackSelected", [trackId]));

        if (!firedScheduledPlay)
        {
            var fraction = totalCount > 0 ? (double)readyCount / totalCount : 0.0;
            effects.Add(new ClientSend(connectionId, "WaitingForPeers", [(long)0, fraction]));
        }

        return effects;
    }

    public IReadOnlyList<HubEffect> OnRequestStop(string connectionId, string group)
    {
        var effects = new List<HubEffect>();
        lock (_lock)
        {
            if (_groups.TryGetValue(group, out var groupSM))
                FireGroup(groupSM, GroupTrigger.Stop, effects);
        }
        effects.Add(new GroupBroadcast(group, "StopPlay", []));
        return effects;
    }

    public IReadOnlyList<HubEffect> OnReportNowPlaying(string connectionId, string group, string trackId, string title, string artist, string? coverUrl)
    {
        _nowPlayings[connectionId] = new ClientNowPlaying(trackId, title, artist, coverUrl);
        var effects = new List<HubEffect>();
        lock (_lock)
        {
            effects.Add(BuildPeersStateEffect(group));
        }
        return effects;
    }

    public IReadOnlyList<HubEffect> OnDisconnect(string connectionId)
    {
        var effects = new List<HubEffect>();
        string? groupName = null;

        lock (_lock)
        {
            if (_clientInfos.TryRemove(connectionId, out var info))
                groupName = info.GroupName;

            _devices.TryRemove(connectionId, out _);
            _progress.TryRemove(connectionId, out _);
            _nowPlayings.TryRemove(connectionId, out _);

            if (groupName is not null && _groups.TryGetValue(groupName, out var groupSM))
            {
                groupSM.Members.Remove(connectionId);
                groupSM.EffectSink = effects.Add;
                try
                {
                    groupSM.Fire(GroupTrigger.MemberLeft);
                    // Re-evaluate: the leaving member may have been the last not-ready
                    // peer holding up a pending play.
                    groupSM.TryAdvanceIfAllReady();
                }
                finally { groupSM.EffectSink = null; }

                effects.Add(BuildPeersStateEffect(groupName));
            }
        }
        return effects;
    }

    private GroupStateMachine GetOrCreateGroup(string groupName) =>
        _groups.GetOrAdd(groupName, name => new GroupStateMachine(
            name,
            connId => _devices.TryGetValue(connId, out var d) ? d.State : DeviceState.Idle,
            _nowMs,
            connId => _nowPlayings.TryGetValue(connId, out var np) ? np.TrackId : null));

    private static void FireGroup(GroupStateMachine group, GroupTrigger trigger, List<HubEffect> effects)
    {
        group.EffectSink = effects.Add;
        try { group.Fire(trigger); }
        finally { group.EffectSink = null; }
    }

    private static void FireDeviceToTargetState(DeviceStateMachine device, DeviceState target)
    {
        DeviceTrigger? trigger = target switch
        {
            DeviceState.Idle => DeviceTrigger.Stopped,
            DeviceState.Splitting => DeviceTrigger.BeginSplit,
            DeviceState.Buffering => DeviceTrigger.BeginBuffer,
            DeviceState.Ready => DeviceTrigger.BufferReady,
            DeviceState.Playing => DeviceTrigger.PlayStarted,
            _ => null,
        };
        if (trigger.HasValue && device.CanFire(trigger.Value))
            device.Fire(trigger.Value);
    }

    private GroupBroadcast BuildPeersStateEffect(string groupName)
    {
        List<object> peers = [];
        if (_groups.TryGetValue(groupName, out var groupSM))
        {
            foreach (var connId in groupSM.Members)
            {
                var name = _clientInfos.TryGetValue(connId, out var ci) ? ci.DeviceName : "Device";
                var devState = _devices.TryGetValue(connId, out var d) ? d.State : DeviceState.Idle;
                var prog = d?.LastProgress ?? 0.0;
                _nowPlayings.TryGetValue(connId, out var np);
                peers.Add(new
                {
                    connectionId = connId,
                    name,
                    phase = devState.ToWireString(),
                    progress = prog,
                    trackId = np?.TrackId ?? "",
                    title = np?.Title ?? "",
                    artist = np?.Artist ?? "",
                    coverUrl = np?.CoverUrl ?? "",
                });
            }
        }
        return new GroupBroadcast(groupName, "PeersStateUpdated", [peers]);
    }
}
