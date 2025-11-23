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