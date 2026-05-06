using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using SyncAudio.Services.Sync;

namespace SyncAudio.Hubs;

public class SyncHub : Hub
{
    private static readonly ConcurrentDictionary<string, HashSet<string>> Groups_ = new();
    private static readonly ConcurrentDictionary<string, ProgressSample> Progress = new();
    private static readonly ConcurrentDictionary<string, ClientInfo> ClientInfos_ = new();
    private static readonly ConcurrentDictionary<string, ClientPhase> ClientPhases_ = new();
    private static readonly ConcurrentDictionary<string, PendingPlayRequest> PendingPlays_ = new();
    private static readonly ConcurrentDictionary<string, ClientNowPlaying> ClientNowPlayings_ = new();
    private static readonly Lock Lock = new();

    private record ClientInfo(string DeviceName, string GroupName);
    private record ClientPhase(string Phase, double Progress);
    private record PendingPlayRequest(string TrackId, string RequestorConnectionId, double LeadSeconds);
    private record ClientNowPlaying(string TrackId, string Title, string Artist, string? CoverUrl);

    public async Task JoinGroup(string group, string? deviceName = null)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, group);
        int count;
        lock (Lock)
        {
            if (!Groups_.ContainsKey(group)) Groups_[group] = [];
            Groups_[group].Add(Context.ConnectionId);
            count = Groups_[group].Count;
        }
        ClientInfos_[Context.ConnectionId] = new ClientInfo(deviceName ?? "Device", group);
        ClientPhases_.TryAdd(Context.ConnectionId, new ClientPhase("idle", 0));

        await Clients.Group(group).SendAsync("MemberCount", count);
        await BroadcastPeersState(group);
    }

    // NTP-style: returns server clock at moment of receipt
    public long Ping() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>
    /// Receive a buffer-progress sample from a client. Used by <see cref="RequestPlay"/>
    /// to compute an adaptive lead time so the slowest peer is ready by the play moment.
    /// A drop in fraction (e.g. setSrc on a new track) re-anchors the speed-estimation
    /// baseline. Also broadcasts peer states to the group so each device can see others'
    /// buffering progress.
    /// </summary>
    public async Task ReportProgress(string group, double p)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var clamped = Math.Clamp(p, 0.0, 1.0);

        Progress.AddOrUpdate(
            Context.ConnectionId,
            _ => new ProgressSample(clamped, nowMs, clamped, nowMs),
            (_, existing) =>
            {
                // A meaningful drop in reported fraction means the client started over
                // (new decode). Re-anchor First* so speed extrapolation reflects the
                // current download, not a mix of old + new.
                if (clamped + 0.05 < existing.LastFraction)
                    return new ProgressSample(clamped, nowMs, clamped, nowMs);
                return existing with { LastFraction = clamped, LastTimestampMs = nowMs };
            });

        ClientPhases_[Context.ConnectionId] =
            new ClientPhase(clamped >= 1.0 ? "ready" : "buffering", clamped);

        await TryTriggerPendingPlay(group);
        await BroadcastPeersState(group);
    }

    /// <summary>
    /// Client reports an explicit phase transition: splitting, buffering, ready, playing, idle.
    /// Broadcasts the updated peer state to the group so all devices see the change.
    /// </summary>
    public async Task ReportPhase(string group, string phase, double progress)
    {
        ClientPhases_[Context.ConnectionId] =
            new ClientPhase(phase, Math.Clamp(progress, 0.0, 1.0));
        await TryTriggerPendingPlay(group);
        await BroadcastPeersState(group);
    }

    public async Task RequestPlay(string group, string trackId, double leadSeconds = 3.0)
    {
        int readyCount, totalCount;
        lock (Lock)
        {
            var members = Groups_.TryGetValue(group, out var m) ? m : [];
            totalCount = members.Count;
            // Reset every peer's phase so they must re-confirm readiness for this
            // specific track before the play fires. The caller keeps its phase —
            // it already has the track buffered and will stay (or become) "ready".
            foreach (var id in members)
                if (id != Context.ConnectionId)
                    ClientPhases_[id] = new ClientPhase("buffering", 0.0);
            var callerReady = ClientPhases_.TryGetValue(Context.ConnectionId, out var cp)
                              && cp.Phase == "ready";
            readyCount = callerReady ? 1 : 0;
            PendingPlays_[group] = new PendingPlayRequest(trackId, Context.ConnectionId, leadSeconds);
        }

        // Ask peers to buffer the new track. Each peer re-reports "ready" once
        // it has the buffer decoded (same track → immediate; different → after split+decode).
        await Clients.OthersInGroup(group).SendAsync("TrackSelected", trackId);

        // Fire immediately when caller is the only member; otherwise wait for peers.
        await TryTriggerPendingPlay(group);
        if (PendingPlays_.ContainsKey(group))
            await Clients.Caller.SendAsync("WaitingForPeers", (long)0,
                (double)readyCount / Math.Max(totalCount, 1));
    }

    public Task RequestStop(string group)
    {
        PendingPlays_.TryRemove(group, out _);
        return Clients.Group(group).SendAsync("StopPlay");
    }

    public async Task ReportNowPlaying(string group, string trackId, string title, string artist, string? coverUrl)
    {
        ClientNowPlayings_[Context.ConnectionId] = new ClientNowPlaying(trackId, title, artist, coverUrl);
        await BroadcastPeersState(group);
    }

    public Task RequestSelectAlbum(string group, string albumRatingKey)
        => Clients.OthersInGroup(group).SendAsync("AlbumSelected", albumRatingKey);

    public Task RequestSelectTrack(string group, string trackId)
        => Clients.OthersInGroup(group).SendAsync("TrackSelected", trackId);

    public Task RequestSelectTrackWithMeta(
        string group, string trackId, string? albumKey,
        string title, string artist, string? coverUrl)
        => Clients.OthersInGroup(group).SendAsync(
            "TrackSelectedWithMeta", trackId, albumKey ?? "", title, artist, coverUrl ?? "");

    public override async Task OnDisconnectedAsync(Exception? ex)
    {
        string? groupName = null;
        lock (Lock)
            foreach (var (g, members) in Groups_)
                if (members.Remove(Context.ConnectionId)) { groupName = g; break; }

        if (ClientInfos_.TryRemove(Context.ConnectionId, out var info))
            groupName ??= info.GroupName;
        ClientPhases_.TryRemove(Context.ConnectionId, out _);
        Progress.TryRemove(Context.ConnectionId, out _);
        ClientNowPlayings_.TryRemove(Context.ConnectionId, out _);

        await base.OnDisconnectedAsync(ex);
        if (groupName is not null)
        {
            await TryTriggerPendingPlay(groupName);
            await BroadcastPeersState(groupName);
        }
    }

    private Task FirePlay(string group, string trackId, double leadSeconds)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var lead = Math.Max(AdaptiveLeadCalculator.MinLeadMs, (long)(leadSeconds * 1000));
        return Clients.Group(group).SendAsync("ScheduledPlay", nowMs + lead, trackId);
    }

    private async Task TryTriggerPendingPlay(string group)
    {
        if (!PendingPlays_.TryGetValue(group, out _)) return;

        bool allReady;
        lock (Lock)
        {
            allReady = Groups_.TryGetValue(group, out var members)
                && members.Count > 0
                && members.All(id => ClientPhases_.TryGetValue(id, out var cp) && cp.Phase == "ready");
        }

        if (allReady && PendingPlays_.TryRemove(group, out var fired))
            await FirePlay(group, fired.TrackId, fired.LeadSeconds);
    }

    private Task BroadcastPeersState(string group)
    {
        List<object> peers;
        lock (Lock)
        {
            peers = Groups_.TryGetValue(group, out var members)
                ? members.Select(connId =>
                {
                    var name = ClientInfos_.TryGetValue(connId, out var ci) ? ci.DeviceName : "Device";
                    var (phase, prog) = ClientPhases_.TryGetValue(connId, out var cp)
                        ? (cp.Phase, cp.Progress) : ("idle", 0.0);
                    ClientNowPlayings_.TryGetValue(connId, out var np);
                    return (object)new
                    {
                        connectionId = connId, name, phase, progress = prog,
                        trackId  = np?.TrackId  ?? "",
                        title    = np?.Title    ?? "",
                        artist   = np?.Artist   ?? "",
                        coverUrl = np?.CoverUrl ?? "",
                    };
                }).ToList()
                : [];
        }
        return Clients.Group(group).SendAsync("PeersStateUpdated", peers);
    }
}
