using Microsoft.AspNetCore.SignalR;
using SyncAudio.Services.Sync.StateMachines;

namespace SyncAudio.Hubs;

/// <summary>
/// Thin SignalR dispatcher. All state-tracking and group-coordination logic lives in
/// <see cref="IPlaybackOrchestrator"/>; the hub's job is to translate SignalR calls into
/// orchestrator method calls and broadcast the returned <see cref="HubEffect"/>s.
/// </summary>
public class SyncHub(IPlaybackOrchestrator orchestrator) : Hub
{

    public Task JoinGroup(string group, string? deviceName = null)
    {
        var effects = orchestrator.OnJoin(Context.ConnectionId, group, deviceName ?? "Device");
        // The orchestrator's GroupBroadcast effects target the SignalR group, but the
        // caller hasn't been added to that group via the underlying SignalR machinery
        // yet. Add to the SignalR group first so MemberCount/PeersStateUpdated reach
        // this connection too.
        return AddToGroupThenApply(group, effects);
    }

    public long Ping() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public Task ReportProgress(string group, double p)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var effects = orchestrator.OnReportProgress(Context.ConnectionId, group, p, nowMs);
        return ApplyEffectsAsync(effects);
    }

    public Task ReportPhase(string group, string phase, double progress)
    {
        var effects = orchestrator.OnReportPhase(Context.ConnectionId, group, phase, progress);
        return ApplyEffectsAsync(effects);
    }

    public Task RequestPlay(string group, string trackId, double leadSeconds = 3.0)
    {
        var effects = orchestrator.OnRequestPlay(Context.ConnectionId, group, trackId, leadSeconds);
        return ApplyEffectsAsync(effects);
    }

    public Task RequestStop(string group)
    {
        var effects = orchestrator.OnRequestStop(Context.ConnectionId, group);
        return ApplyEffectsAsync(effects);
    }

    public Task ReportNowPlaying(string group, string trackId, string title, string artist, string? coverUrl)
    {
        var effects = orchestrator.OnReportNowPlaying(
            Context.ConnectionId, group, trackId, title, artist, coverUrl);
        return ApplyEffectsAsync(effects);
    }

    // Pure-broadcast methods — no state implication, leave as direct sends.
    public Task RequestSelectAlbum(string group, string albumRatingKey)
        => Clients.OthersInGroup(group).SendAsync("AlbumSelected", albumRatingKey);

    public Task RequestSelectTrack(string group, string trackId)
        => Clients.OthersInGroup(group).SendAsync("TrackSelected", trackId);

    public Task RequestSelectTrackWithMeta(
        string group, string trackId, string? albumKey,
        string title, string artist, string? coverUrl)
        => Clients.OthersInGroup(group).SendAsync(
            "TrackSelectedWithMeta", trackId, albumKey ?? "", title, artist, coverUrl ?? "");

    public Task RequestSelectFormat(string group, string format)
        => Clients.OthersInGroup(group).SendAsync("FormatSelected", format);

    public override async Task OnDisconnectedAsync(Exception? ex)
    {
        var effects = orchestrator.OnDisconnect(Context.ConnectionId);
        await base.OnDisconnectedAsync(ex);
        await ApplyEffectsAsync(effects);
    }

    private async Task AddToGroupThenApply(string group, IReadOnlyList<HubEffect> effects)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, group);
        await ApplyEffectsAsync(effects);
    }

    private async Task ApplyEffectsAsync(IReadOnlyList<HubEffect> effects)
    {
        foreach (var effect in effects)
        {
            switch (effect)
            {
                case GroupBroadcast g:
                    await Clients.Group(g.Group).SendCoreAsync(g.Method, g.Args);
                    break;
                case OthersInGroupBroadcast o:
                    await Clients.OthersInGroup(o.Group).SendCoreAsync(o.Method, o.Args);
                    break;
                case ClientSend c:
                    await Clients.Client(c.ConnectionId).SendCoreAsync(c.Method, c.Args);
                    break;
            }
        }
    }
}
