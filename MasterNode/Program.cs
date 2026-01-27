// Master Node
using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Linq;
using Shared.Logging;
using Shared.Messages;
using Shared.Constants;
using Shared.Node;

internal class Program
{
    private static readonly Dictionary<string, Node> _nodes = new();
    private static readonly object _nodesLock = new();
    private static readonly TimeSpan _heartbeatTimeout = TimeSpan.FromSeconds(7);
    private static readonly string MasterNodeId = NodeIdProvider.GetNodeId();
    private static volatile string? _lastLoadedDatasetId = null;
    private static volatile int _lastLoadedTotalChunks = 0;

    // chunkId -> worker (owner)
    private static readonly Dictionary<int, Node> _chunkOwners = new();
    private static readonly object _datasetLock = new();




    private static readonly object _loadProgressLock = new();
    private static volatile bool _loadInProgress = false;
    private static volatile string _loadStatus = "";
    private static volatile int _chunksSent = 0;
    private static volatile int _chunksAcked = 0;
    private static long _linesSent = 0;
    private static long _linesAcked = 0;
    private static volatile string _currentDatasetId = "";



    private static long _fileTotalBytes = 0;
    private static long _fileBytesRead = 0;



    private static async Task Main(string[] args)
    {
        Logger.Init("Master");

        Console.WriteLine($"MASTER NODE STARTED");
        Console.WriteLine($"MASTER NODE UUID: {MasterNodeId}");
        Console.WriteLine($"Discovery broadcast on UDP port {Ports.Discovery}");
        Console.WriteLine($"Heartbeat listener on UDP port {Ports.Heartbeat}");

        // Get local IPv4
        var iface = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(n =>
                n.OperationalStatus == OperationalStatus.Up &&
                n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                n.GetIPProperties().UnicastAddresses
                    .Any(u => u.Address.AddressFamily == AddressFamily.InterNetwork)
            );

        if (iface == null)
        {
            throw new Exception("No active network interface with IPv4 found.");
        }

        var unicast = iface.GetIPProperties().UnicastAddresses
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

        // Start background tasks
        _ = BroadcastDiscoveryLoop(localIp, broadcastIp);
        _ = HeartbeatListenerLoop();
        _ = LostWorkerCheckerLoop();

        // Start CLI (blocks until user quits)
        await CliLoop();
    }

    // DISCOVERY BROADCAST LOOP
    private static async Task BroadcastDiscoveryLoop(IPAddress localIp, IPAddress broadcastIp)
    {
        using var udp = new UdpClient(new IPEndPoint(localIp, 0));
        udp.EnableBroadcast = true;

        var discoveryMsg = new DiscoveryMessage
        {
            SenderId = MasterNodeId
        };

        string json = JsonSerializer.Serialize(discoveryMsg);
        byte[] data = Encoding.UTF8.GetBytes(json);

        while (true)
        {
            try
            {
                await udp.SendAsync(data, data.Length, new IPEndPoint(broadcastIp, Ports.Discovery));
                // Logger.Info("Broadcasted DISCOVER");
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

            lock (_nodesLock)
            {
                if (!_nodes.ContainsKey(hb.NodeId))
                {
                    _nodes[hb.NodeId] = new Node(
                        NodeType.Worker,
                        hb.HostName,
                        hb.NodeId,
                        result.RemoteEndPoint.Address
                    );
                }

                _nodes[hb.NodeId].UpdateLastHeartbeat(DateTime.UtcNow);
            }


            // Logger.Info($"Heartbeat from {hb.NodeId}[{result.RemoteEndPoint.Address}] at {hb.IpAddress}");
        }
    }

    // LOST-WORKER CHECKER LOOP
    private static async Task LostWorkerCheckerLoop()
    {
        while (true)
        {
            var now = DateTime.UtcNow;

            lock (_nodesLock)
            {
                foreach (var node in _nodes.Values.ToList())
                {
                    if (now - node.LastHeartbeat > _heartbeatTimeout)
                    {
                        Logger.Warn($"Worker lost: {node.Id} ({node.IpAddress})");
                        _nodes.Remove(node.Id);
                    }
                }
            }


            await Task.Delay(2000);
        }
    }

    // -----------------
    // CLI
    // -----------------
    private static async Task CliLoop()
    {
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            _requestedQuit = true;
        };

        // Non-blocking, live-refresh console loop.
        // Keeps the node count + states at the top while you type commands.
        var input = new StringBuilder();
        string? lastMessage = null;

        Console.TreatControlCAsInput = false;
        Console.CursorVisible = true;

        var nextRender = DateTime.UtcNow;
        while (!_requestedQuit)
        {
            // Render at ~4 FPS (enough to feel live, low flicker)
            if (DateTime.UtcNow >= nextRender)
            {
                RenderScreen(input.ToString(), lastMessage);
                nextRender = DateTime.UtcNow.AddMilliseconds(250);
            }

            if (Console.KeyAvailable)
            {
                var key = Console.ReadKey(intercept: true);

                if (key.Key == ConsoleKey.Enter)
                {
                    var cmd = input.ToString().Trim();
                    input.Clear();

                    if (!string.IsNullOrWhiteSpace(cmd))
                    {
                        lastMessage = await ExecuteCommandAsync(cmd);
                    }
                    else
                    {
                        lastMessage = null;
                    }

                    // Render immediately after command
                    RenderScreen(input.ToString(), lastMessage);
                }
                else if (key.Key == ConsoleKey.Backspace)
                {
                    if (input.Length > 0)
                        input.Length -= 1;
                }
                else if (key.Key == ConsoleKey.Escape)
                {
                    input.Clear();
                }
                else if (!char.IsControl(key.KeyChar))
                {
                    input.Append(key.KeyChar);
                }
            }

            await Task.Delay(15);
        }

        Console.Clear();
        Console.WriteLine("Bye.");
    }

    private static volatile bool _requestedQuit = false;

    private static async Task<string?> ExecuteCommandAsync(string cmd)
    {
        // Menu shortcuts (single key)
        if (cmd == "1") cmd = "nodes";
        if (cmd == "2") cmd = "load";
        if (cmd == "3") cmd = "plan";
        if (cmd.Equals("q", StringComparison.OrdinalIgnoreCase)) cmd = "quit";

        var parts = cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var head = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";

        switch (head)
        {
            case "help":
            case "h":
            case "?":
                return "Commands: 1/nodes, 2/load (placeholder), 3/plan (placeholder), clear, help, quit";

            case "nodes":
                return DescribeNodes();

            case "clear":
            case "cls":
                return null; // next render will clear anyway

            case "load":
                {
                    // load <path> <linesPerChunk>
                    var path = parts.Length >= 2 ? parts[1] : "./data/out.csv";
                    int linesPerChunk = (parts.Length >= 3 && int.TryParse(parts[2], out var n)) ? n : 20000;

                    if (_loadInProgress)
                        return "Load already in progress.";

                    _loadInProgress = true;
                    _loadStatus = "Starting...";
                    _chunksSent = 0;
                    _chunksAcked = 0;
                    _linesSent = 0;
                    _linesAcked = 0;

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var msg = await LoadAndDistributeStreamingAsync(path, linesPerChunk);
                            _loadStatus = msg;
                        }
                        catch (Exception ex)
                        {
                            _loadStatus = "Load failed: " + ex.Message;
                        }
                        finally
                        {
                            _loadInProgress = false;
                        }
                    });

                    return $"Load started: {path} (linesPerChunk={linesPerChunk})";

                }
            case "plan":
                return "Stage planning is not implemented yet.";

            case "quit":
            case "exit":
                _requestedQuit = true;
                return "Exiting...";

            default:
                return $"Unknown command: '{cmd}'. Type 'help'.";
        }
    }

    private static async Task<string> LoadAndDistributeStreamingAsync(string csvPath, int linesPerChunk)
    {
        List<Node> workers;
        lock (_nodesLock)
        {
            workers = _nodes.Values.ToList();
        }

        if (workers.Count == 0)
            return "Cannot load: no active workers.";

        if (!File.Exists(csvPath))
            return $"File not found: {csvPath}";

        _fileTotalBytes = new FileInfo(csvPath).Length;
        Interlocked.Exchange(ref _fileBytesRead, 0);


        if (linesPerChunk < 1)
            return "linesPerChunk must be >= 1.";

        var datasetId = Guid.NewGuid().ToString("N");
        int chunkId = 0;
        int workerIndex = 0;

        _currentDatasetId = datasetId;

        using var sr = new StreamReader(csvPath);

        // Read header once (we ignore it; worker Map already skips header safely)
        var header = await sr.ReadLineAsync();
        if (header == null)
            return "CSV is empty.";

        var buffer = new List<string>(linesPerChunk);

        async Task FlushAsync()
        {
            if (buffer.Count == 0) return;

            var worker = workers[workerIndex];
            var lines = buffer.ToArray();  // only one chunk at a time -> bounded RAM
            buffer.Clear();

            _chunksSent++;
            _linesSent += lines.Length;
            _loadStatus = $"Loading dataset={datasetId}  sentChunks={_chunksSent}  ackedChunks={_chunksAcked}";


            var res = await SendChunkToWorkerStreamingAsync(worker, datasetId, chunkId, lines);
            if (!res.ok)
                throw new Exception($"Chunk {chunkId} failed on {worker.Id}: {res.msg}");

            _chunksAcked++;
            _linesAcked += lines.Length;
            _loadStatus = $"Loading dataset={datasetId}  sentChunks={_chunksSent}  ackedChunks={_chunksAcked}";

            lock (_datasetLock)
            {
                _chunkOwners[chunkId] = worker;
            }

            _loadStatus = $"Loaded dataset {datasetId}. Chunks={chunkId}. linesPerChunk={linesPerChunk}. Workers={workers.Count}.";

            chunkId++;
            workerIndex = (workerIndex + 1) % workers.Count;
        }

        while (true)
        {
            var line = await sr.ReadLineAsync();
            Interlocked.Exchange(ref _fileBytesRead, sr.BaseStream.Position);
            if (line == null) break;

            buffer.Add(line);

            if (buffer.Count >= linesPerChunk)
                await FlushAsync();
        }

        await FlushAsync();

        lock (_datasetLock)
        {
            _lastLoadedDatasetId = datasetId;
            _lastLoadedTotalChunks = chunkId;
        }

        return $"Loaded dataset {datasetId}. Chunks={chunkId}. linesPerChunk={linesPerChunk}. Workers={workers.Count}.";
    }

    private static async Task<(bool ok, string msg)> SendChunkToWorkerStreamingAsync(
        Node worker,
        string datasetId,
        int chunkId,
        string[] lines)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await client.ConnectAsync(worker.IpAddress, Ports.Jobs, cts.Token);
            using var stream = client.GetStream();

            var sha = ComputeSha256OfLines(lines);

            var msg = new DataChunkMessage
            {
                Type = Messages.DataChunkMessageString,
                DatasetId = datasetId,
                JobId = datasetId,      // TEMP: keep compatibility with older code paths
                ChunkId = chunkId,
                TotalChunks = -1,       // unknown in streaming mode
                Lines = lines,
                RowCount = lines.Length,
                Sha256 = sha
            };

            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(msg));
            await WriteFrameAsync(stream, bytes, cts.Token);

            var ackFrame = await ReadFrameAsync(stream, cts.Token);
            var ackJson = Encoding.UTF8.GetString(ackFrame);
            var ack = JsonSerializer.Deserialize<DataChunkAckMessage>(ackJson);

            if (ack == null || ack.Type != Messages.DataChunkAckMessageString)
                return (false, "Invalid ACK.");

            if (!ack.Ok)
                return (false, ack.Error);

            if (ack.ChunkId != chunkId)
                return (false, "ACK chunkId mismatch.");

            if (ack.RowCount != lines.Length || ack.Sha256 != sha)
                return (false, "ACK integrity mismatch.");

            return (true, "OK");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }


    private static int PartitionFor(string genre, int movieId, int reducers)
    {
        // stable hash: use string + movieId
        unchecked
        {
            int h = 17;
            h = (h * 31) + genre.GetHashCode(StringComparison.Ordinal);
            h = (h * 31) + movieId.GetHashCode();
            h = h & 0x7fffffff;
            return h % reducers;
        }
    }

    private static async Task<(string wid, bool ok, string msg, int received)> SendShuffleToReducerAsync(
        Node reducer, string jobId, int reducerIndex, CombinedPair[] pairs)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await client.ConnectAsync(reducer.IpAddress, Ports.Jobs, cts.Token);
            using var stream = client.GetStream();

            var msg = new ShufflePartitionMessage
            {
                Type = Messages.ShufflePartitionMessageString,
                JobId = jobId,
                ReducerIndex = reducerIndex,
                Pairs = pairs
            };

            await WriteFrameAsync(stream, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(msg)), cts.Token);

            var ackFrame = await ReadFrameAsync(stream, cts.Token);
            var ackJson = Encoding.UTF8.GetString(ackFrame);

            using var doc = JsonDocument.Parse(ackJson);
            if (!doc.RootElement.TryGetProperty("Type", out var typeProp))
                return (reducer.Id, false, "Shuffle response missing Type.", 0);

            var type = typeProp.GetString();
            if (type != Messages.ShuffleAckMessageString)
                return (reducer.Id, false, $"Unexpected shuffle response Type={type}", 0);

            var ack = JsonSerializer.Deserialize<ShuffleAckMessage>(ackJson);
            if (ack == null) return (reducer.Id, false, "Invalid SHUFFLE_ACK.", 0);
            if (!ack.Ok) return (reducer.Id, false, ack.Error, 0);

            return (reducer.Id, true, "OK", ack.ReceivedPairs);
        }
        catch (Exception ex)
        {
            return (reducer.Id, false, ex.Message, 0);
        }
    }

    private static async Task<(string wid, bool ok, string msg, GenreTopMovie[] top)> SendReduceRequestAsync(
        Node reducer, JobSpec job)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await client.ConnectAsync(reducer.IpAddress, Ports.Jobs, cts.Token);
            using var stream = client.GetStream();

            var req = new ReduceRequestMessage { Type = Messages.ReduceRequestMessageString, Job = job };
            await WriteFrameAsync(stream, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(req)), cts.Token);

            var resFrame = await ReadFrameAsync(stream, cts.Token);
            var resJson = Encoding.UTF8.GetString(resFrame);

            using var doc = JsonDocument.Parse(resJson);
            if (!doc.RootElement.TryGetProperty("Type", out var typeProp))
                return (reducer.Id, false, "Reduce response missing Type.", Array.Empty<GenreTopMovie>());

            var type = typeProp.GetString();
            if (type != Messages.ReduceResultMessageString)
                return (reducer.Id, false, $"Unexpected reduce response Type={type}", Array.Empty<GenreTopMovie>());

            var res = JsonSerializer.Deserialize<ReduceResultMessage>(resJson);
            if (res == null) return (reducer.Id, false, "Invalid REDUCE_RESULT.", Array.Empty<GenreTopMovie>());
            if (!res.Ok) return (reducer.Id, false, res.Error, Array.Empty<GenreTopMovie>());

            return (reducer.Id, true, "OK", res.Top ?? Array.Empty<GenreTopMovie>());
        }
        catch (Exception ex)
        {
            return (reducer.Id, false, ex.Message, Array.Empty<GenreTopMovie>());
        }
    }

    private static string Bar(long done, long total, int width = 30)
    {
        if (total <= 0) return "[" + new string('-', width) + "]";
        double frac = Math.Clamp((double)done / total, 0.0, 1.0);
        int filled = (int)Math.Round(frac * width);
        return "[" + new string('#', filled) + new string('-', width - filled) + $"] {(frac * 100):0}%";
    }


    private static void RenderScreen(string currentInput, string? lastMessage)
    {
        Console.Clear();

        var now = DateTime.UtcNow;
        var snapshot = GetNodeSnapshot(now);

        Console.WriteLine($"MASTER {MasterNodeId}");
        Console.WriteLine($"Workers: {snapshot.Count}   (Heartbeat timeout: {_heartbeatTimeout.TotalSeconds:0}s)   UTC: {now:HH:mm:ss}");
        Console.WriteLine(new string('-', Math.Max(20, Console.WindowWidth - 1)));

        if (snapshot.Count == 0)
        {
            Console.WriteLine("No active workers (waiting for heartbeats)...");
        }
        else
        {
            // Small table
            Console.WriteLine("ID (short)        IP               Host                Last seen   State");
            Console.WriteLine("---------------------------------------------------------------");
            foreach (var n in snapshot.OrderBy(s => s.State).ThenBy(s => s.SecondsSinceLastSeen))
            {
                Console.WriteLine($"{n.ShortId,-15} {n.Ip,-16} {TrimTo(n.Host, 18),-18} {n.SecondsSinceLastSeen,6:0.0}s   {n.State}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("Menu: [1] nodes  [2] load   [3] plan    [map] map    [help] ?    [q] quit");
        Console.WriteLine();


        if (_loadInProgress)
        {
            long doneBytes = Interlocked.Read(ref _fileBytesRead);
            long totalBytes = _fileTotalBytes;

            Console.WriteLine($"[LOAD] File progress {Bar(doneBytes, totalBytes)}  {doneBytes}/{totalBytes} bytes");
            Console.WriteLine($"[LOAD] Chunks: {_chunksAcked}/{_chunksSent}   Lines: {Interlocked.Read(ref _linesAcked)}/{Interlocked.Read(ref _linesSent)}");
            Console.WriteLine($"[LOAD] Status: {_loadStatus}");

        }
        else if (!string.IsNullOrWhiteSpace(_loadStatus))
        {
            Console.WriteLine($"[LOAD] {_loadStatus}");
            Console.WriteLine();
        }


        if (!string.IsNullOrWhiteSpace(lastMessage))
        {
            Console.WriteLine($"> {lastMessage}");
            Console.WriteLine();
        }

        Console.Write("> ");
        Console.Write(currentInput);
    }

    private static string DescribeNodes()
    {
        var snapshot = GetNodeSnapshot(DateTime.UtcNow);
        if (snapshot.Count == 0) return "No active workers.";

        var healthy = snapshot.Count(s => s.State == NodeUiState.Healthy);
        var stale = snapshot.Count(s => s.State == NodeUiState.Stale);
        return $"Workers: {snapshot.Count} (Healthy: {healthy}, Stale: {stale}).";
    }

    private static List<NodeSnapshot> GetNodeSnapshot(DateTime now)
    {
        lock (_nodesLock)
        {
            return _nodes.Values
                .Select(n =>
                {
                    var age = now - n.LastHeartbeat;
                    var state = age.TotalSeconds <= 2
                        ? NodeUiState.Healthy
                        : NodeUiState.Stale;

                    return new NodeSnapshot(
                        ShortId: n.Id.Length > 12 ? n.Id[..12] : n.Id,
                        Ip: n.IpAddress?.ToString() ?? "?",
                        Host: n.HostName ?? "?",
                        SecondsSinceLastSeen: (float)age.TotalSeconds,
                        State: state
                    );
                })
                .ToList();
        }
    }

    private static string TrimTo(string s, int max)
        => s.Length <= max ? s : s[..Math.Max(0, max - 1)] + "…";

    private enum NodeUiState
    {
        Healthy = 0,
        Stale = 1,
    }

    private sealed record NodeSnapshot(
        string ShortId,
        string Ip,
        string Host,
        float SecondsSinceLastSeen,
        NodeUiState State
    );

    private static async Task WriteFrameAsync(NetworkStream stream, byte[] payload, CancellationToken ct)
    {
        byte[] len = BitConverter.GetBytes(payload.Length);
        await stream.WriteAsync(len, 0, len.Length, ct);
        await stream.WriteAsync(payload, 0, payload.Length, ct);
        await stream.FlushAsync(ct);
    }

    private static async Task<byte[]> ReadFrameAsync(NetworkStream stream, CancellationToken ct)
    {
        byte[] lenBuf = new byte[4];
        int read = 0;
        while (read < 4)
        {
            int r = await stream.ReadAsync(lenBuf, read, 4 - read, ct);
            if (r == 0) throw new IOException("Stream closed while reading length.");
            read += r;
        }

        int len = BitConverter.ToInt32(lenBuf, 0);
        if (len <= 0 || len > 100_000_000) throw new IOException($"Invalid frame length: {len}");

        byte[] payload = new byte[len];
        int offset = 0;
        while (offset < len)
        {
            int r = await stream.ReadAsync(payload, offset, len - offset, ct);
            if (r == 0) throw new IOException("Stream closed while reading payload.");
            offset += r;
        }
        return payload;
    }

    private static string ComputeSha256OfLines(string[] lines)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var text = string.Join("\n", lines ?? Array.Empty<string>());
        var bytes = Encoding.UTF8.GetBytes(text);
        var hash = sha.ComputeHash(bytes);
        return Convert.ToHexString(hash);
    }

    private static string[][] SplitIntoChunks(string[] lines, int chunks)
    {
        var result = new string[chunks][];
        int total = lines.Length;
        int baseSize = total / chunks;
        int rem = total % chunks;

        int offset = 0;
        for (int i = 0; i < chunks; i++)
        {
            int size = baseSize + (i < rem ? 1 : 0);
            result[i] = lines.Skip(offset).Take(size).ToArray();
            offset += size;
        }
        return result;
    }

}
