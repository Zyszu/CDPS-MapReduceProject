namespace Shared.Messages;

public class DiscoveryMessage
{
    public string Type { get; } = "DISCOVERY";
    public string SenderId { get; set; } = "";
}

public class DiscoveryResponse
{
    public string Type { get; } = "DISCOVERY_RESPONSE";
    public string NodeId { get; set; } = "";
    public string IpAddress { get; set; } = "";
}

public class HeartbeatMessage
{
    public string Type { get; } = "HEARTHBEAT";
    public string NodeId { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}