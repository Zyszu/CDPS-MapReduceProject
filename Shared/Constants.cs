namespace Shared.Constants;

public static class Ports
{
    public const int Discovery = 5000;
    public const int Heartbeat = 5001;
    public const int Jobs = 5002;
}

public static class Messages
{
    public const string DiscoveryMessageString = "DISCOVERY";
    public const string HeartbeatMessageString = "HEARTHBEAT";

    public const string DataChunkMessageString = "DATA_CHUNK";
    public const string DataChunkAckMessageString = "DATA_CHUNK_ACK";

    // MapReduce
    public const string MapRequestMessageString = "MAP_REQUEST";
    public const string MapResultMessageString = "MAP_RESULT";
}
