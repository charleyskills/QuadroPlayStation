using Stateless;

namespace SyncAudio.Components.Pages.NowPlaying.StateMachines;

/// <summary>
/// Per-circuit playback state machine. Owned by <see cref="NowPlayingState"/>.
///
/// Transitions:
/// <code>
///   Disconnected --Joined--> Idle
///   Idle --TrackSelected--> Splitting
///   Splitting --SplitDone--> Buffering
///   Splitting --SplitFailed--> Idle
///   Buffering --BufferReady--> Ready
///   Buffering --PlayStarted--> Playing       (HTML5 fallback: play before canplay)
///   Ready --PlayStarted--> Playing
///   Ready --TrackSelected--> Splitting
///   Playing --PlaybackEnded--> Ready
///   Playing --TrackSelected--> Splitting
///   any --Disconnected--> Disconnected
/// </code>
/// </summary>
public sealed class LocalDeviceStateMachine
{
    private readonly StateMachine<LocalDeviceState, LocalDeviceTrigger> _sm;

    public LocalDeviceState State => _sm.State;

    /// <summary>Fired after every transition. (source, destination, trigger).</summary>
    public event Action<LocalDeviceState, LocalDeviceState, LocalDeviceTrigger>? Transitioned;

    /// <summary>Invoked when entering <see cref="LocalDeviceState.Playing"/>. Used to clear
    /// transient flags (e.g. <see cref="NowPlayingState.IsWaitingForPeers"/>) without coupling
    /// the machine to the state object.</summary>
    public event Action? PlaybackBegan;

    public LocalDeviceStateMachine(LocalDeviceState initialState = LocalDeviceState.Disconnected)
    {
        _sm = new StateMachine<LocalDeviceState, LocalDeviceTrigger>(initialState, FiringMode.Queued);
        Configure();
        _sm.OnTransitioned(t => Transitioned?.Invoke(t.Source, t.Destination, t.Trigger));
    }

    private void Configure()
    {
        _sm.Configure(LocalDeviceState.Disconnected)
            .PermitReentry(LocalDeviceTrigger.Disconnected)
            .Permit(LocalDeviceTrigger.Joined, LocalDeviceState.Idle);

        _sm.Configure(LocalDeviceState.Idle)
            .PermitReentry(LocalDeviceTrigger.Joined)
            .PermitReentry(LocalDeviceTrigger.SplitFailed)
            .PermitReentry(LocalDeviceTrigger.PlaybackEnded)
            .Permit(LocalDeviceTrigger.TrackSelected, LocalDeviceState.Splitting)
            .Permit(LocalDeviceTrigger.Disconnected, LocalDeviceState.Disconnected);

        _sm.Configure(LocalDeviceState.Splitting)
            .PermitReentry(LocalDeviceTrigger.TrackSelected)
            .Permit(LocalDeviceTrigger.SplitDone, LocalDeviceState.Buffering)
            .Permit(LocalDeviceTrigger.SplitFailed, LocalDeviceState.Idle)
            .Permit(LocalDeviceTrigger.Disconnected, LocalDeviceState.Disconnected);

        _sm.Configure(LocalDeviceState.Buffering)
            .Permit(LocalDeviceTrigger.BufferReady, LocalDeviceState.Ready)
            // HTML5 fallback edge case: play event can arrive before canplay.
            .Permit(LocalDeviceTrigger.PlayStarted, LocalDeviceState.Playing)
            .Permit(LocalDeviceTrigger.TrackSelected, LocalDeviceState.Splitting)
            .Permit(LocalDeviceTrigger.SplitFailed, LocalDeviceState.Idle)
            .Permit(LocalDeviceTrigger.Disconnected, LocalDeviceState.Disconnected);

        _sm.Configure(LocalDeviceState.Ready)
            .PermitReentry(LocalDeviceTrigger.BufferReady)
            .PermitReentry(LocalDeviceTrigger.PlaybackEnded) // idempotent — already not playing
            .Permit(LocalDeviceTrigger.PlayStarted, LocalDeviceState.Playing)
            .Permit(LocalDeviceTrigger.TrackSelected, LocalDeviceState.Splitting)
            .Permit(LocalDeviceTrigger.Disconnected, LocalDeviceState.Disconnected);

        _sm.Configure(LocalDeviceState.Playing)
            .OnEntry(() => PlaybackBegan?.Invoke())
            .PermitReentry(LocalDeviceTrigger.PlayStarted)
            .PermitReentry(LocalDeviceTrigger.BufferReady)
            .Permit(LocalDeviceTrigger.PlaybackEnded, LocalDeviceState.Ready)
            .Permit(LocalDeviceTrigger.TrackSelected, LocalDeviceState.Splitting)
            .Permit(LocalDeviceTrigger.Disconnected, LocalDeviceState.Disconnected);
    }

    public void Fire(LocalDeviceTrigger trigger) => _sm.Fire(trigger);
    public bool CanFire(LocalDeviceTrigger trigger) => _sm.CanFire(trigger);
}
