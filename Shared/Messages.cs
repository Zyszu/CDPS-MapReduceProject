namespace Shared.Messages;

using Shared.Constants;

public class DiscoveryMessage
{
    public string Type { get; } = Messages.DiscoveryMessageString;
    public string SenderId { get; set; } = "";
}

public class HeartbeatMessage
{
    public string Type { get; } = Messages.HeartbeatMessageString;
    public string HostName { get; set; } = "";
    public string NodeId { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}


public class DataChunkMessage
{
    public string Type { get; set; } = Messages.DataChunkMessageString;

    public string DatasetId { get; set; } = "";
    public string JobId { get; set; } = "";
    public int ChunkId { get; set; }
    public int TotalChunks { get; set; }
    public string[] Lines { get; set; } = Array.Empty<string>();
    public int RowCount { get; set; }
    public string Sha256 { get; set; } = "";
}

public class DataChunkAckMessage
{
    public string Type { get; set; } = Messages.DataChunkAckMessageString;

    public string DatasetId { get; set; } = "";
    public string JobId { get; set; } = "";
    public int ChunkId { get; set; }
    public int RowCount { get; set; }
    public string Sha256 { get; set; } = "";
    public bool Ok { get; set; }
    public string Error { get; set; } = "";
}


public class JobSpec
{
    public string JobId { get; set; } = "";
    public int TopN { get; set; } = 10;
    public long? FromTimestamp { get; set; }
    public long? ToTimestamp { get; set; }

    // For activeusers: optional genre filter (exact match, case-insensitive)
    public string? GenreFilter { get; set; }
}

public class CombinedPair
{
    public string Genre { get; set; } = "";
    public int MovieId { get; set; }
    public double SumRatings { get; set; }
    public int CountRatings { get; set; }

    // Leave empty to reduce memory/network; resolve titles later if needed
    public string Title { get; set; } = "";
}

public class MapRequestMessage
{
    public string Type { get; set; } = Messages.MapRequestMessageString;
    public string DatasetId { get; set; } = "";
    public JobSpec Job { get; set; } = new();
    public int ChunkId { get; set; }
}


public class MapResultMessage
{
    public string Type { get; set; } = Messages.MapResultMessageString;
    public string JobId { get; set; } = "";
    public int ChunkId { get; set; }
    public CombinedPair[] Pairs { get; set; } = Array.Empty<CombinedPair>();
    public bool Ok { get; set; } = true;
    public string Error { get; set; } = "";
}

public class ShufflePartitionMessage
{
    public string Type { get; set; } = Messages.ShufflePartitionMessageString;
    public string JobId { get; set; } = "";
    public int ReducerIndex { get; set; }
    public CombinedPair[] Pairs { get; set; } = Array.Empty<CombinedPair>();
}

public class ShuffleAckMessage
{
    public string Type { get; set; } = Messages.ShuffleAckMessageString;

    public string JobId { get; set; } = "";
    public int ReducerIndex { get; set; }
    public int ReceivedPairs { get; set; }

    public bool Ok { get; set; } = true;
    public string Error { get; set; } = "";
}

public class ReduceRequestMessage
{
    public string Type { get; set; } = Messages.ReduceRequestMessageString;

    public JobSpec Job { get; set; } = new();
}

public class GenreTopMovie
{
    public string Genre { get; set; } = "";
    public int MovieId { get; set; }
    public double AvgRating { get; set; }
    public int CountRatings { get; set; }
}

public class ReduceResultMessage
{
    public string Type { get; set; } = Messages.ReduceResultMessageString;
    public string JobId { get; set; } = "";
    public GenreTopMovie[] Top { get; set; } = Array.Empty<GenreTopMovie>();
    public bool Ok { get; set; } = true;
    public string Error { get; set; } = "";
}

public class UserCountPair
{
    public int UserId { get; set; }
    public int Count { get; set; }
}

public class MapUsersRequestMessage
{
    public string Type { get; set; } = Messages.MapUsersRequestMessageString;
    public string DatasetId { get; set; } = "";
    public JobSpec Job { get; set; } = new();
    public int ChunkId { get; set; }
}

public class MapUsersResultMessage
{
    public string Type { get; set; } = Messages.MapUsersResultMessageString;
    public string JobId { get; set; } = "";
    public int ChunkId { get; set; }
    public UserCountPair[] Pairs { get; set; } = Array.Empty<UserCountPair>();
    public bool Ok { get; set; } = true;
    public string Error { get; set; } = "";
}

public class ShuffleUsersPartitionMessage
{
    public string Type { get; set; } = Messages.ShuffleUsersPartitionMessageString;
    public string JobId { get; set; } = "";
    public int ReducerIndex { get; set; }
    public UserCountPair[] Pairs { get; set; } = Array.Empty<UserCountPair>();
}

public class ShuffleUsersAckMessage
{
    public string Type { get; set; } = Messages.ShuffleUsersAckMessageString;
    public string JobId { get; set; } = "";
    public int ReducerIndex { get; set; }
    public int ReceivedPairs { get; set; }
    public bool Ok { get; set; } = true;
    public string Error { get; set; } = "";
}

public class ReduceUsersRequestMessage
{
    public string Type { get; set; } = Messages.ReduceUsersRequestMessageString;
    public JobSpec Job { get; set; } = new();
}

public class UserActivity
{
    public int UserId { get; set; }
    public int Count { get; set; }
}

public class ReduceUsersResultMessage
{
    public string Type { get; set; } = Messages.ReduceUsersResultMessageString;
    public string JobId { get; set; } = "";
    public UserActivity[] Top { get; set; } = Array.Empty<UserActivity>();
    public bool Ok { get; set; } = true;
    public string Error { get; set; } = "";
}
