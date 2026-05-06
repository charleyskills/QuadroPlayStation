namespace SyncAudio.Services.Sync.StateMachines;

/// <summary>
/// Pure functional core of the SyncHub. Each method consumes a hub-call's inputs and
/// returns a list of <see cref="HubEffect"/> that the hub then broadcasts. No
/// <c>IHubContext</c> dependency, no async I/O — all SignalR calls happen in the hub.
/// </summary>
public interface IPlaybackOrchestrator
{
    IReadOnlyList<HubEffect> OnJoin(string connectionId, string group, string deviceName);
    IReadOnlyList<HubEffect> OnReportProgress(string connectionId, string group, double fraction, long nowMs);
    IReadOnlyList<HubEffect> OnReportPhase(string connectionId, string group, string wirePhase, double progress);
    IReadOnlyList<HubEffect> OnRequestPlay(string connectionId, string group, string trackId, double leadSeconds);
    IReadOnlyList<HubEffect> OnRequestStop(string connectionId, string group);
    IReadOnlyList<HubEffect> OnReportNowPlaying(string connectionId, string group, string trackId, string title, string artist, string? coverUrl);
    IReadOnlyList<HubEffect> OnDisconnect(string connectionId);
}
