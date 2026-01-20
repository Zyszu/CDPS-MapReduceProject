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
