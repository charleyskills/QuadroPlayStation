using Microsoft.Extensions.Logging;

namespace SyncAudio.Services.Sync.StateMachines;

/// <summary>
/// One-to-one mapping between <see cref="DeviceState"/> and the phase strings that travel
/// over the SignalR wire (kept stable for JS compatibility — see syncAudio.js _reportPhase).
/// </summary>
public static class WireStrings
{
    public const string Idle = "idle";
    public const string Splitting = "splitting";
    public const string Buffering = "buffering";
    public const string Ready = "ready";
    public const string Playing = "playing";

    public static string ToWireString(this DeviceState state) => state switch
    {
        DeviceState.Idle => Idle,
        DeviceState.Splitting => Splitting,
        DeviceState.Buffering => Buffering,
        DeviceState.Ready => Ready,
        DeviceState.Playing => Playing,
        _ => Idle,
    };

    /// <summary>
    /// Parse a phase string from JS into a typed state. Unknown strings fall through to
    /// <see cref="DeviceState.Idle"/> — never throw, since a JS-side typo would otherwise
    /// spam logs and break sync. The caller may log unknowns at <c>Debug</c>.
    /// </summary>
    public static DeviceState ParseDeviceWire(string? wire, ILogger? logger = null)
    {
        switch (wire)
        {
            case Idle: return DeviceState.Idle;
            case Splitting: return DeviceState.Splitting;
            case Buffering: return DeviceState.Buffering;
            case Ready: return DeviceState.Ready;
            case Playing: return DeviceState.Playing;
            default:
                logger?.LogDebug("Unknown wire phase '{Wire}', defaulting to Idle.", wire);
                return DeviceState.Idle;
        }
    }
}
