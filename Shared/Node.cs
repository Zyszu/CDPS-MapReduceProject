namespace Shared.Node;

using System.Net;


public enum NodeType
{
    Master,
    Worker
}

public class Node
{
    public NodeType NodeRole { get; set; }
    public string HostName { get; set; }
    public string Id { get; set; }
    public IPAddress IpAddress { get; set; }
    public DateTime LastHeartbeat { get; set; }

    public Node(NodeType NodeRole, string HostName, string Id, IPAddress IpAddress)
    {
        this.NodeRole   = NodeRole;
        this.HostName   = HostName;
        this.Id         = Id;
        this.IpAddress  = IpAddress;
    }

    public void updateLastHeartbeat(DateTime dt)
    {
        this.LastHeartbeat = dt;
    }

}