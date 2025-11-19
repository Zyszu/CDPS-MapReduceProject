using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Text;

// Get your active network interface
var iface = NetworkInterface.GetAllNetworkInterfaces()
    .First(n => n.OperationalStatus == OperationalStatus.Up &&
                n.NetworkInterfaceType != NetworkInterfaceType.Loopback);

var props = iface.GetIPProperties();
var unicast = props.UnicastAddresses
    .First(u => u.Address.AddressFamily == AddressFamily.InterNetwork);

var localIP = unicast.Address;
var mask = unicast.IPv4Mask;

// Compute broadcast address
byte[] ipBytes = localIP.GetAddressBytes();
byte[] maskBytes = mask.GetAddressBytes();
byte[] broadcastBytes = new byte[4];

for (int i = 0; i < 4; i++)
    broadcastBytes[i] = (byte)(ipBytes[i] | (maskBytes[i] ^ 255));

var broadcastIP = new IPAddress(broadcastBytes);

Console.WriteLine("Broadcast IP = " + broadcastIP);

// Bind to interface
var udp = new UdpClient(new IPEndPoint(localIP, 0));
udp.EnableBroadcast = true;

var data = Encoding.UTF8.GetBytes("Hello LAN!");

while (true)
{
    udp.Send(data, data.Length, new IPEndPoint(broadcastIP, 5000));
    Console.WriteLine("Broadcast sent!");
    
    await Task.Delay(1000);
}