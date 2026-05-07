namespace SyncAudio.Core.Services.Sync.StateMachines;

/// <summary>
/// Describes a SignalR broadcast that the hub should perform after the orchestrator
/// returns. Keeps the orchestrator pure (no IHubContext dependency) and trivially
/// unit-testable.
/// </summary>
public abstract record HubEffect;

/// <summary>Send to every connection in <paramref name="Group"/>.</summary>
public sealed record GroupBroadcast(string Group, string Method, object?[] Args) : HubEffect;

/// <summary>Send to every connection in <paramref name="Group"/> except the caller.</summary>
public sealed record OthersInGroupBroadcast(string Group, string Method, object?[] Args) : HubEffect;

/// <summary>Send to a single connection by ID.</summary>
public sealed record ClientSend(string ConnectionId, string Method, object?[] Args) : HubEffect;
