using System.Net;
using System.Net.Sockets;
using System.Text;

internal class Program
{
    private static void Main(string[] args)
    {
        Console.WriteLine("Listening for UDP messages on port 5000...");

        var udp = new UdpClient(5000);  // Bind to port 5000
        var sender = new IPEndPoint(IPAddress.Any, 0);

        while (true)
        {
            var data = udp.Receive(ref sender);
            string message = Encoding.UTF8.GetString(data);

            if (message == "Hello LAN!")
            {
                Console.WriteLine($"[{DateTime.Now}] Got it from {sender.Address}");
            }
        }
    }
}