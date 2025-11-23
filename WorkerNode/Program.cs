using Shared.Constants;
using Shared.Logging;
using Shared.Messages;
using Shared.Networking;
using Shared.Node;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

internal class Program
{

    private static readonly string WorkerNodeId = NodeIdProvider.GetNodeId();

    private static async Task Main(string[] args)
    {
        Logger.Init("Worker");

        Console.WriteLine("Worker node started.");
        Console.WriteLine($"Listening for discovery messages on UDP port {Ports.Discovery}...");

        using var udp = new UdpClient(Ports.Discovery);
        var connectionState = ConnectionState.SearchingMaster;
        IPAddress? masterIp = null;

        while (true)
        {
            if (connectionState == ConnectionState.SearchingMaster)
            {
                // run discovery loop and update state
                var result = await DiscoveryLoop(udp);
                if (result.state == ConnectionState.CommunicatingMaster)
                {
                    connectionState = result.state;
                    masterIp = result.masterIp;

                    // Start heartbeat loop (fire and forget)
                    _ = HeartbeatLoop(masterIp);
                }
            }

            if (connectionState == ConnectionState.CommunicatingMaster)
            {
                // later: listen for work
                await Task.Delay(100);
            }
        }
    }

    private static async Task<(ConnectionState state, IPAddress masterIp)> DiscoveryLoop(UdpClient udp)
    {
        var result = await udp.ReceiveAsync();
        string rawMessage = Encoding.UTF8.GetString(result.Buffer);

        Logger.Info($"Received: {rawMessage} from {result.RemoteEndPoint.Address}");

        // Try to interpret it as a discovery message
        DiscoveryMessage? discovery;

        try
        {
            discovery = JsonSerializer.Deserialize<DiscoveryMessage>(rawMessage);
        }
        catch
        {
            return (ConnectionState.SearchingMaster, IPAddress.None);
        }

        if (discovery is null)
        {
            return (ConnectionState.SearchingMaster, IPAddress.None);
        }

        if (discovery.Type == Messages.DiscoveryMessageString)
        {
            Logger.Info("Master discovery received.");
            return (ConnectionState.CommunicatingMaster, result.RemoteEndPoint.Address);
        }

        return (ConnectionState.SearchingMaster, IPAddress.None);
    }

    private static async Task HeartbeatLoop(IPAddress masterIp)
    {
        using var udp = new UdpClient();
        int port = Ports.Heartbeat;

        while (true)
        {
            var hb = new HeartbeatMessage
            {
                HostName    = Environment.MachineName,
                NodeId      = WorkerNodeId,
                IpAddress   = masterIp.ToString(),
                Timestamp   = DateTime.UtcNow
            };

            string json = JsonSerializer.Serialize(hb);
            byte[] data = Encoding.UTF8.GetBytes(json);

            await udp.SendAsync(data, data.Length, new IPEndPoint(masterIp, port));
            Logger.Info("Heartbeat sent");

            await Task.Delay(2000); // every 2 seconds
        }
    }
}
