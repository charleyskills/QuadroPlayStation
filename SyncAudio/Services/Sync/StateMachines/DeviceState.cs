namespace SyncAudio.Services.Sync.StateMachines;

public enum DeviceState
{
    Idle,
    Splitting,
    Buffering,
    Ready,
    Playing,
}
