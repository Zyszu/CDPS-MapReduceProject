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

    public const string ShufflePartitionMessageString = "SHUFFLE_PARTITION";
    public const string ShuffleAckMessageString = "SHUFFLE_ACK";
    public const string ReduceRequestMessageString = "REDUCE_REQUEST";
    public const string ReduceResultMessageString = "REDUCE_RESULT";

    // MapReduce - Most Active Users
    public const string MapUsersRequestMessageString = "MAP_USERS_REQUEST";
    public const string MapUsersResultMessageString = "MAP_USERS_RESULT";
    public const string ShuffleUsersPartitionMessageString = "SHUFFLE_USERS_PARTITION";
    public const string ShuffleUsersAckMessageString = "SHUFFLE_USERS_ACK";
    public const string ReduceUsersRequestMessageString = "REDUCE_USERS_REQUEST";
    public const string ReduceUsersResultMessageString = "REDUCE_USERS_RESULT";

}
