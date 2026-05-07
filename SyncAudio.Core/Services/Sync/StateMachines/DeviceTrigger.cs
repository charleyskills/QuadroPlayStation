namespace SyncAudio.Core.Services.Sync.StateMachines;

public enum DeviceTrigger
{
    BeginSplit,
    SplitDone,
    SplitFailed,
    BeginBuffer,
    BufferReady,
    PlayStarted,
    Stopped,
}
