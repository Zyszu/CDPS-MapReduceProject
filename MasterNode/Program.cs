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
    private static volatile string? _lastLoadedJobId = null;
    private static volatile int _lastLoadedTotalChunks = 0;
    private static List<Node> _lastLoadedWorkers = new();
    private static readonly object _jobLock = new();
    

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
                // Optionally allow path override: load ../data/out.csv
                var path = parts.Length >= 2 ? parts[1] : "../data/out.csv";
                return await LoadAndDistributeAsync(path);

            case "plan":
                return "Stage planning is not implemented yet.";

            case "quit":
            case "exit":
                _requestedQuit = true;
                return "Exiting...";
            case "map":
                int topN = 10;
                long? fromTs = null;
                long? toTs = null;

                if (parts.Length >= 2 && int.TryParse(parts[1], out var parsedTopN))
                    topN = parsedTopN;

                if (parts.Length >= 3 && long.TryParse(parts[2], out var parsedFrom))
                    fromTs = parsedFrom;

                if (parts.Length >= 4 && long.TryParse(parts[3], out var parsedTo))
                    toTs = parsedTo;

                return await MapPhaseAsync(topN, fromTs, toTs);


            default:
                return $"Unknown command: '{cmd}'. Type 'help'.";
        }
    }

    private static async Task<string> MapPhaseAsync(int topN, long? fromTimestamp, long? toTimestamp)
    {
        string? jobId;
        List<Node> workers;
        int totalChunks;

        lock (_jobLock)
        {
            jobId = _lastLoadedJobId;
            workers = _lastLoadedWorkers.ToList();
            totalChunks = _lastLoadedTotalChunks;
        }

        if (string.IsNullOrWhiteSpace(jobId) || workers.Count == 0 || totalChunks == 0)
            return "No loaded job found. Run 'load' first.";

        // Build JobSpec for MAP
        var job = new JobSpec
        {
            JobId = jobId,
            TopN = topN,
            FromTimestamp = fromTimestamp,
            ToTimestamp = toTimestamp
        };

        // One MAP request per worker/chunk
        var tasks = new List<Task<(string workerId, bool ok, string msg, int pairs)>>();

        for (int i = 0; i < workers.Count; i++)
        {
            int chunkId = i; // IMPORTANT: must match LoadAndDistributeAsync chunk assignment
            tasks.Add(SendMapRequestToWorkerAsync(workers[i], job, chunkId));
        }

        var results = await Task.WhenAll(tasks);

        int okCount = results.Count(r => r.ok);
        int totalPairs = results.Where(r => r.ok).Sum(r => r.pairs);
        var errors = results.Where(r => !r.ok).ToList();

        if (errors.Count == 0)
        {
            return $"MAP complete for JobId={jobId}. Workers OK: {okCount}/{workers.Count}. Total combined pairs: {totalPairs}.";
        }

        var errText = string.Join(" | ", errors.Select(e => $"{e.workerId}: {e.msg}"));
        return $"MAP partial failure for JobId={jobId}. OK: {okCount}/{workers.Count}. TotalPairs(from OK): {totalPairs}. Errors: {errText}";
    }

    private static async Task<(string workerId, bool ok, string msg, int pairs)> SendMapRequestToWorkerAsync(
    Node worker,
    JobSpec job,
    int chunkId)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            await client.ConnectAsync(worker.IpAddress, Ports.Jobs, cts.Token);
            using var stream = client.GetStream();

            // 1) Send MAP_REQUEST
            var req = new MapRequestMessage
            {
                Type = Shared.Constants.Messages.MapRequestMessageString, // IMPORTANT if Type is settable
                Job = job,
                ChunkId = chunkId
            };

            var reqJson = JsonSerializer.Serialize(req);
            var reqBytes = Encoding.UTF8.GetBytes(reqJson);

            await WriteFrameAsync(stream, reqBytes, cts.Token);

            // 2) Read response
            var resFrame = await ReadFrameAsync(stream, cts.Token);
            var resJson = Encoding.UTF8.GetString(resFrame);

            // 3) Inspect "Type" before deserializing into a concrete class
            using var doc = JsonDocument.Parse(resJson);
            if (!doc.RootElement.TryGetProperty("Type", out var typeProp))
                return (worker.Id, false, "Response missing Type field.", 0);

            var type = typeProp.GetString();

            // 4) Handle MAP_RESULT
            if (type == Shared.Constants.Messages.MapResultMessageString)
            {
                var res = JsonSerializer.Deserialize<MapResultMessage>(resJson);
                if (res == null)
                    return (worker.Id, false, "Invalid MAP_RESULT JSON.", 0);

                if (!res.Ok)
                    return (worker.Id, false, $"Worker MAP error: {res.Error}", 0);

                if (!string.Equals(res.JobId, job.JobId, StringComparison.Ordinal) || res.ChunkId != chunkId)
                    return (worker.Id, false, $"MAP_RESULT mismatch (jobId/chunkId). Got jobId={res.JobId}, chunkId={res.ChunkId}", 0);

                int pairs = res.Pairs?.Length ?? 0;
                return (worker.Id, true, "OK", pairs);
            }

            // 5) If worker returns DATA_CHUNK_ACK during MAP, report clearly
            if (type == Shared.Constants.Messages.DataChunkAckMessageString)
            {
                var ack = JsonSerializer.Deserialize<DataChunkAckMessage>(resJson);
                if (ack == null)
                    return (worker.Id, false, "Invalid DATA_CHUNK_ACK JSON.", 0);

                return (worker.Id, false,
                    $"Worker returned DATA_CHUNK_ACK during MAP (unexpected). Ok={ack.Ok}, Error={ack.Error}",
                    0);
            }

            // 6) Unknown response type
            return (worker.Id, false, $"Unknown response Type: {type}", 0);
        }
        catch (Exception ex)
        {
            return (worker.Id, false, ex.Message, 0);
        }
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
        Console.WriteLine("Menu: [1] nodes   [2] load   [3] plan [map] map [help]   [q] quit");
        Console.WriteLine();

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



        private static async Task<string> LoadAndDistributeAsync(string csvPath)
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

        var allLines = await File.ReadAllLinesAsync(csvPath);
        if (allLines.Length <= 1)
            return "CSV has no data rows.";

        // Keep header (optional for later), chunk only data lines
        var header = allLines[0];
        var dataLines = allLines.Skip(1).ToArray();

        // Equal chunks by row count
        int n = workers.Count;
        var chunks = SplitIntoChunks(dataLines, n);

        var jobId = Guid.NewGuid().ToString("N");

        // Send in parallel (one chunk per worker)
        var tasks = new List<Task<(string workerId, bool ok, string msg)>>();
        for (int i = 0; i < n; i++)
        {
            var worker = workers[i];
            var chunkLines = chunks[i];
            int chunkId = i;

            tasks.Add(SendChunkToWorkerAsync(worker, jobId, chunkId, n, chunkLines));
        }

        var results = await Task.WhenAll(tasks);

        int okCount = results.Count(r => r.ok);
        var errors = results.Where(r => !r.ok).ToList();

        if (errors.Count == 0)
        {
            // IMPORTANT: remember last successful load so "map" can use it
            lock (_jobLock)
            {
                _lastLoadedJobId = jobId;
                _lastLoadedTotalChunks = n;
                _lastLoadedWorkers = workers; // chunkId i was sent to workers[i]
            }

            return $"Loaded {dataLines.Length} rows and distributed to {okCount}/{n} workers. JobId={jobId}";
        }


        var errText = string.Join(" | ", errors.Select(e => $"{e.workerId}: {e.msg}"));
        return $"Partial failure: {okCount}/{n} workers OK. Errors: {errText}";
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

    private static async Task<(string workerId, bool ok, string msg)> SendChunkToWorkerAsync(
        Node worker,
        string jobId,
        int chunkId,
        int totalChunks,
        string[] lines)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            await client.ConnectAsync(worker.IpAddress, Ports.Jobs, cts.Token);

            using var stream = client.GetStream();

            var sha = ComputeSha256OfLines(lines);

            var msg = new DataChunkMessage
            {
                JobId = jobId,
                ChunkId = chunkId,
                TotalChunks = totalChunks,
                Lines = lines,
                RowCount = lines.Length,
                Sha256 = sha
            };

            var json = JsonSerializer.Serialize(msg);
            var bytes = Encoding.UTF8.GetBytes(json);

            await WriteFrameAsync(stream, bytes, cts.Token);

            // Read ACK and verify
            var ackFrame = await ReadFrameAsync(stream, cts.Token);
            var ackJson = Encoding.UTF8.GetString(ackFrame);
            var ack = JsonSerializer.Deserialize<DataChunkAckMessage>(ackJson);

            if (ack == null || ack.Type != Shared.Constants.Messages.DataChunkAckMessageString)
                return (worker.Id, false, "Invalid ACK.");

            if (!ack.Ok)
                return (worker.Id, false, $"Worker rejected chunk: {ack.Error}");

            if (ack.JobId != jobId || ack.ChunkId != chunkId)
                return (worker.Id, false, "ACK jobId/chunkId mismatch.");

            if (ack.RowCount != lines.Length || ack.Sha256 != sha)
                return (worker.Id, false, "ACK integrity mismatch (hash/rowCount).");

            return (worker.Id, true, "OK");
        }
        catch (Exception ex)
        {
            return (worker.Id, false, ex.Message);
        }
    }


}
