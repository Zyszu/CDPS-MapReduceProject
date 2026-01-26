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
            {
                // Optionally allow path override: load ../data/out.csv
                var path = parts.Length >= 2 ? parts[1] : "../data/out.csv";
                return await LoadAndDistributeAsync(path);
            }

            case "plan":
                return "Stage planning is not implemented yet.";

            case "quit":
            case "exit":
                _requestedQuit = true;
                return "Exiting...";
            case "map":
            {
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
            }
            case "run":
            {
                // run <path> [topN] [fromTs] [toTs]
                if (parts.Length < 2) return "Usage: run <csvPath> [topN] [fromTs] [toTs]";

                string path = parts[1];
                int topN = (parts.Length >= 3 && int.TryParse(parts[2], out var t)) ? t : 10;
                long? fromTs = (parts.Length >= 4 && long.TryParse(parts[3], out var f)) ? f : null;
                long? toTs = (parts.Length >= 5 && long.TryParse(parts[4], out var to)) ? to : null;

                return await RunMapReduceAsync(path, topN, fromTs, toTs);
            }


            default:
                return $"Unknown command: '{cmd}'. Type 'help'.";
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

    private static async Task<string> RunMapReduceAsync(string csvPath, int topN, long? fromTs, long? toTs)
    {
        // 1) Load (your existing function)
        var loadRes = await LoadAndDistributeAsync(csvPath);
        Logger.Info(loadRes);

        // Pull job info
        string? jobId;
        List<Node> workers;
        int n;

        lock (_jobLock)
        {
            jobId = _lastLoadedJobId;
            workers = _lastLoadedWorkers.ToList();
            n = _lastLoadedTotalChunks;
        }

        if (string.IsNullOrWhiteSpace(jobId) || workers.Count == 0 || n == 0)
            return "Load did not set a valid job state; cannot run.";

        var job = new JobSpec { JobId = jobId, TopN = topN, FromTimestamp = fromTs, ToTimestamp = toTs };

        // 2) MAP: collect all CombinedPairs from all workers
        var mapTasks = new List<Task<(string wid, bool ok, string msg, CombinedPair[] pairs)>>();
        for (int i = 0; i < workers.Count; i++)
        {
            int chunkId = i;
            mapTasks.Add(SendMapRequestToWorkerReturnPairsAsync(workers[i], job, chunkId));
        }

        var mapResults = await Task.WhenAll(mapTasks);
        var mapErrors = mapResults.Where(r => !r.ok).ToList();
        if (mapErrors.Count > 0)
            return "MAP failed: " + string.Join(" | ", mapErrors.Select(e => $"{e.wid}: {e.msg}"));

        var allPairs = mapResults.SelectMany(r => r.pairs ?? Array.Empty<CombinedPair>()).ToArray();
        Logger.Info($"MAP collected combined pairs: {allPairs.Length}");

        // 3) SHUFFLE: partition pairs and send to reducers (reducers = workers.Count)
        int reducers = workers.Count;
        var buckets = new List<CombinedPair>[reducers];
        for (int i = 0; i < reducers; i++) buckets[i] = new List<CombinedPair>();

        foreach (var p in allPairs)
        {
            int idx = PartitionFor(p.Genre, p.MovieId, reducers);
            buckets[idx].Add(p);
        }

        var shuffleTasks = new List<Task<(string wid, bool ok, string msg, int received)>>();
        for (int r = 0; r < reducers; r++)
        {
            shuffleTasks.Add(SendShuffleToReducerAsync(workers[r], jobId, r, buckets[r].ToArray()));
        }

        var shuffleResults = await Task.WhenAll(shuffleTasks);
        var shuffleErrors = shuffleResults.Where(s => !s.ok).ToList();
        if (shuffleErrors.Count > 0)
            return "SHUFFLE failed: " + string.Join(" | ", shuffleErrors.Select(e => $"{e.wid}: {e.msg}"));

        Logger.Info($"SHUFFLE complete. Sent total pairs: {allPairs.Length}");

        // 4) REDUCE: ask each reducer for topN-per-genre results
        var reduceTasks = workers.Select(w => SendReduceRequestAsync(w, job)).ToArray();
        var reduceResults = await Task.WhenAll(reduceTasks);

        var reduceErrors = reduceResults.Where(r => !r.ok).ToList();
        if (reduceErrors.Count > 0)
            return "REDUCE failed: " + string.Join(" | ", reduceErrors.Select(e => $"{e.wid}: {e.msg}"));

        var allTop = reduceResults.SelectMany(r => r.top ?? Array.Empty<GenreTopMovie>()).ToList();

        // 5) FINAL merge: topN per genre across reducers
        var finalPerGenre = allTop
            .GroupBy(x => x.Genre)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(x => x.AvgRating)
                    .ThenByDescending(x => x.CountRatings)
                    .ThenBy(x => x.MovieId)
                    .Take(topN)
                    .ToList()
            );

        // 6) Print (simple text)
        var sb = new StringBuilder();
        sb.AppendLine($"JobId={jobId}  TopN={topN}  Workers={workers.Count}");
        if (fromTs.HasValue || toTs.HasValue)
            sb.AppendLine($"Time filter: from={fromTs?.ToString() ?? "-"} to={toTs?.ToString() ?? "-"}");
        sb.AppendLine();

        foreach (var genre in finalPerGenre.Keys.OrderBy(x => x))
        {
            sb.AppendLine($"== {genre} ==");
            int rank = 1;
            foreach (var item in finalPerGenre[genre])
            {
                sb.AppendLine($"{rank,2}. {item.Title} (movieId={item.MovieId}) avg={item.AvgRating:F3} n={item.CountRatings}");
                rank++;
            }
            sb.AppendLine();
        }

        var resultText = sb.ToString();

        var resultsDir = Path.Combine(AppContext.BaseDirectory, "results");
        Directory.CreateDirectory(resultsDir);

        var filePath = Path.Combine(resultsDir, $"job_{jobId}.txt");
        await File.WriteAllTextAsync(filePath, resultText);

        return $"Job {jobId} finished. Results written to {filePath}";

    }

    private static async Task<(string wid, bool ok, string msg, CombinedPair[] pairs)> SendMapRequestToWorkerReturnPairsAsync(
        Node worker, JobSpec job, int chunkId)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await client.ConnectAsync(worker.IpAddress, Ports.Jobs, cts.Token);
            using var stream = client.GetStream();

            var req = new MapRequestMessage { Type = Messages.MapRequestMessageString, Job = job, ChunkId = chunkId };
            await WriteFrameAsync(stream, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(req)), cts.Token);

            var resFrame = await ReadFrameAsync(stream, cts.Token);
            var resJson = Encoding.UTF8.GetString(resFrame);

            using var doc = JsonDocument.Parse(resJson);
            if (!doc.RootElement.TryGetProperty("Type", out var typeProp))
                return (worker.Id, false, "Response missing Type.", Array.Empty<CombinedPair>());

            var type = typeProp.GetString();
            if (type != Messages.MapResultMessageString)
                return (worker.Id, false, $"Unexpected response Type={type}", Array.Empty<CombinedPair>());

            var res = JsonSerializer.Deserialize<MapResultMessage>(resJson);
            if (res == null) return (worker.Id, false, "Invalid MAP_RESULT.", Array.Empty<CombinedPair>());
            if (!res.Ok) return (worker.Id, false, res.Error, Array.Empty<CombinedPair>());

            return (worker.Id, true, "OK", res.Pairs ?? Array.Empty<CombinedPair>());
        }
        catch (Exception ex)
        {
            return (worker.Id, false, ex.Message, Array.Empty<CombinedPair>());
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
        Console.WriteLine("Menu: [1] nodes   [2] load   [3] plan [map] map [run] run [help]   [q] quit");
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
