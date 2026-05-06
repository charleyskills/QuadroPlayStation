using Stateless;

namespace SyncAudio.Services.Sync.StateMachines;

/// <summary>
/// Per-connection state machine. Replaces the magic-string <c>ClientPhase</c> record
/// in the previous SyncHub. Permissive on "begin"-style transitions (BeginSplit,
/// BeginBuffer) so that out-of-order phase reports from a buggy/lagging JS client
/// can't deadlock — the trigger is accepted from any state and lands in the right one.
/// </summary>
public sealed class DeviceStateMachine
{
    private readonly StateMachine<DeviceState, DeviceTrigger> _sm;

    public string ConnectionId { get; }
    public DeviceState State => _sm.State;

    public string? CurrentTrackId { get; set; }
    public double LastProgress { get; set; }

    /// <summary>Fired after every transition. (source, destination, trigger).</summary>
    public event Action<DeviceState, DeviceState, DeviceTrigger>? Transitioned;

    public DeviceStateMachine(string connectionId, DeviceState initialState = DeviceState.Idle)
    {
        ConnectionId = connectionId;
        _sm = new StateMachine<DeviceState, DeviceTrigger>(initialState, FiringMode.Queued);
        Configure();
        _sm.OnTransitioned(t => Transitioned?.Invoke(t.Source, t.Destination, t.Trigger));
    }

    private void Configure()
    {
        _sm.Configure(DeviceState.Idle)
            .PermitReentry(DeviceTrigger.Stopped)
            .PermitReentry(DeviceTrigger.SplitFailed)
            .Permit(DeviceTrigger.BeginSplit, DeviceState.Splitting)
            .Permit(DeviceTrigger.BeginBuffer, DeviceState.Buffering)
            .Permit(DeviceTrigger.SplitDone, DeviceState.Buffering)
            .Permit(DeviceTrigger.BufferReady, DeviceState.Ready)
            .Permit(DeviceTrigger.PlayStarted, DeviceState.Playing);

        _sm.Configure(DeviceState.Splitting)
            .PermitReentry(DeviceTrigger.BeginSplit)
            .Permit(DeviceTrigger.SplitDone, DeviceState.Buffering)
            .Permit(DeviceTrigger.SplitFailed, DeviceState.Idle)
            .Permit(DeviceTrigger.BeginBuffer, DeviceState.Buffering)
            .Permit(DeviceTrigger.BufferReady, DeviceState.Ready)
            .Permit(DeviceTrigger.Stopped, DeviceState.Idle)
            .Permit(DeviceTrigger.PlayStarted, DeviceState.Playing);

        _sm.Configure(DeviceState.Buffering)
            .Permit(DeviceTrigger.BufferReady, DeviceState.Ready)
            .Permit(DeviceTrigger.Stopped, DeviceState.Idle)
            .PermitReentry(DeviceTrigger.BeginBuffer)
            .PermitReentry(DeviceTrigger.SplitDone)
            .Permit(DeviceTrigger.BeginSplit, DeviceState.Splitting)
            .Permit(DeviceTrigger.SplitFailed, DeviceState.Idle)
            .Permit(DeviceTrigger.PlayStarted, DeviceState.Playing);

        _sm.Configure(DeviceState.Ready)
            .Permit(DeviceTrigger.PlayStarted, DeviceState.Playing)
            .Permit(DeviceTrigger.Stopped, DeviceState.Idle)
            .Permit(DeviceTrigger.BeginBuffer, DeviceState.Buffering)
            .Permit(DeviceTrigger.BeginSplit, DeviceState.Splitting)
            .PermitReentry(DeviceTrigger.BufferReady);

        _sm.Configure(DeviceState.Playing)
            .Permit(DeviceTrigger.Stopped, DeviceState.Idle)
            .Permit(DeviceTrigger.BeginBuffer, DeviceState.Buffering)
            .Permit(DeviceTrigger.BeginSplit, DeviceState.Splitting)
            .Permit(DeviceTrigger.BufferReady, DeviceState.Ready)
            .PermitReentry(DeviceTrigger.PlayStarted);
    }

    public void Fire(DeviceTrigger trigger) => _sm.Fire(trigger);
    public bool CanFire(DeviceTrigger trigger) => _sm.CanFire(trigger);
}
