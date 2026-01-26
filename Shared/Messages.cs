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
    public string Type { get; } = Messages.DataChunkMessageString;

    public string JobId { get; set; } = "";
    public int ChunkId { get; set; }
    public int TotalChunks { get; set; }

    // Integrity fields
    public string Sha256 { get; set; } = "";
    public int RowCount { get; set; }

    // Payload (raw CSV lines for this chunk, no header)
    public string[] Lines { get; set; } = Array.Empty<string>();
}

public class DataChunkAckMessage
{
    public string Type { get; } = Messages.DataChunkAckMessageString;

    public string JobId { get; set; } = "";
    public int ChunkId { get; set; }

    // What worker computed after receipt
    public string Sha256 { get; set; } = "";
    public int RowCount { get; set; }

    public bool Ok { get; set; }
    public string Error { get; set; } = "";
}

public class JobSpec
{
    public string JobId { get; set; } = "";
    public int TopN { get; set; } = 10;

    // Optional timestamp filter (unix seconds)
    public long? FromTimestamp { get; set; }
    public long? ToTimestamp { get; set; }
}

public class CombinedPair
{
    public string Genre { get; set; } = "";
    public int MovieId { get; set; }
    public double SumRatings { get; set; }
    public int CountRatings { get; set; }

    // Optional but very handy for output
    public string Title { get; set; } = "";
}

public class MapRequestMessage
{
    public string Type { get; set; } = Messages.MapRequestMessageString;

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
    public int ReducerIndex { get; set; }   // which reducer (0..R-1)
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
    public string Title { get; set; } = "";
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
