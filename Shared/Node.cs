namespace Shared.Node;

using System.Net;
using System.IO;


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

    public void UpdateLastHeartbeat(DateTime dt)
    {
        this.LastHeartbeat = dt;
    }
}

public static class NodeIdProvider
    {
        private static readonly string NodeIdPath =
            "/var/lib/cdps/nodeid";

        private static string? _cached;

        public static string GetNodeId()
        {
            if (_cached != null)
                return _cached;

            try
            {
                // If file exists use stored ID
                if (File.Exists(NodeIdPath))
                {
                    _cached = File.ReadAllText(NodeIdPath).Trim();
                    if (!string.IsNullOrEmpty(_cached))
                        return _cached;
                }

                // Otherwise generate new UUID
                _cached = Guid.NewGuid().ToString();

                // Ensure directory exists
                Directory.CreateDirectory(Path.GetDirectoryName(NodeIdPath)!);

                // Save for next reboot
                File.WriteAllText(NodeIdPath, _cached);

                return _cached;
            }
            catch
            {
                // If something goes wrong, fallback (still stable per run)
                _cached = Guid.NewGuid().ToString();
                return _cached;
            }
        }
    }