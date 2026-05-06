namespace SyncAudio.Services.Sync.StateMachines;

public enum GroupTrigger
{
    MemberJoined,
    MemberLeft,
    PlayRequested,
    AllMembersReady,
    Stop,
    TrackChanged,
}
