namespace Shared.Networking;

public enum ConnectionState
{
    SearchingMaster,
    CommunicatingMaster,
    LostMaster,
    Reconnecting,
    Idle,
    ProcessingTask
}