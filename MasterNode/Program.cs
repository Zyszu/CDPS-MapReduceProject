using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using Shared.Logging;
using Shared.Messages;
using Shared.Constants;

internal class Program
{
    private static readonly Dictionary<string, DateTime> _lastHeartbeat = new();
    private static readonly TimeSpan _heartbeatTimeout = TimeSpan.FromSeconds(7);

    private static async Task Main(string[] args)
    {
        Logger.Init("Master");

        Console.WriteLine("MASTER NODE STARTED");
        Console.WriteLine($"Discovery broadcast on UDP port {Ports.Discovery}");
        Console.WriteLine($"Heartbeat listener on UDP port {Ports.Heartbeat}");

        // Get local IPv4
        var iface = NetworkInterface.GetAllNetworkInterfaces()
            .First(n => n.OperationalStatus == OperationalStatus.Up &&
                        n.NetworkInterfaceType != NetworkInterfaceType.Loopback);

        var props = iface.GetIPProperties();
        var unicast = props.UnicastAddresses
            .First(u => u.Address.AddressFamily == AddressFamily.InterNetwork);

        var localIp = unicast.Address;
        var mask = unicast.IPv4Mask;

        // Compute broadcast IP
        byte[] ipBytes = localIp.GetAddressBytes();
        byte[] maskBytes = mask.GetAddressBytes();
        byte[] broadcastBytes = new byte[4];

        for (int i = 0; i < 4; i++)
            broadcastBytes[i] = (byte)(ipBytes[i] | (maskBytes[i] ^ 255));

        var broadcastIp = new IPAddress(broadcastBytes);
        Console.WriteLine($"Broadcast IP = {broadcastIp}");

        // Start async tasks
        _ = BroadcastDiscoveryLoop(localIp, broadcastIp);
        _ = HeartbeatListenerLoop();
        _ = LostWorkerCheckerLoop();

        // Keep master alive
        await Task.Delay(-1);
    }

    // DISCOVERY BROADCAST LOOP
    private static async Task BroadcastDiscoveryLoop(IPAddress localIp, IPAddress broadcastIp)
    {
        using var udp = new UdpClient(new IPEndPoint(localIp, 0));
        udp.EnableBroadcast = true;

        var discoveryMsg = new DiscoveryMessage
        {
            SenderId = "Master"
        };

        string json = JsonSerializer.Serialize(discoveryMsg);
        byte[] data = Encoding.UTF8.GetBytes(json);

        while (true)
        {
            try
            {
                await udp.SendAsync(data, data.Length, new IPEndPoint(broadcastIp, Ports.Discovery));
                Logger.Info("Broadcasted DISCOVER");
            }
            catch (Exception ex)
            {
                Logger.Error($"Discovery broadcast failed: {ex.Message}");
            }

            await Task.Delay(1000);
        }
    }

    // HEARTBEAT RECEIVER LOOP
    private static async Task HeartbeatListenerLoop()
    {
        using var udp = new UdpClient(Ports.Heartbeat);

        while (true)
        {
            var result = await udp.ReceiveAsync();
            string raw = Encoding.UTF8.GetString(result.Buffer);

            HeartbeatMessage? hb;

            try
            {
                hb = JsonSerializer.Deserialize<HeartbeatMessage>(raw);
            }
            catch
            {
                continue;
            }

            if (hb == null)
                continue;

            _lastHeartbeat[hb.NodeId] = DateTime.UtcNow;

            Logger.Info($"Heartbeat from {hb.NodeId} at {hb.IpAddress}");
        }
    }

    // LOST-WORKER CHECKER LOOP
    private static async Task LostWorkerCheckerLoop()
    {
        while (true)
        {
            var now = DateTime.UtcNow;

            foreach (var kvp in _lastHeartbeat.ToList())
            {
                var nodeId = kvp.Key;
                var lastSeen = kvp.Value;

                if (now - lastSeen > _heartbeatTimeout)
                {
                    Logger.Warn($"Worker LOST: {nodeId}");
                    _lastHeartbeat.Remove(nodeId);
                }
            }

            await Task.Delay(2000);
        }
    }
}
