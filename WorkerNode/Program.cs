// Worker Node
using Shared.Constants;
using Shared.Logging;
using Shared.Messages;
using Shared.Networking;
using Shared.Node;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Globalization;


internal class Program
{

    private static readonly string WorkerNodeId = NodeIdProvider.GetNodeId();
    private static readonly Dictionary<string, Dictionary<int, string>> _chunkFiles = new();
    private static readonly object _chunkFilesLock = new();
    private static readonly object _dataLock = new();
    private static volatile string? _currentJobId = null;

    // jobId -> (genre,movieId) -> (sum,count)
    private static readonly Dictionary<string, Dictionary<(string genre, int movieId), (double sum, int count)>> _shuffleStore
        = new();
    private static readonly object _shuffleLock = new();

    // jobId -> userId -> count
    private static readonly Dictionary<string, Dictionary<int, int>> _shuffleStoreUsers = new();
    private static readonly object _shuffleUsersLock = new();



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
            var result = await DiscoveryLoop(udp);
            if (result.state == ConnectionState.SearchingMaster)
            {
                // run discovery loop and update state

                if (result.state == ConnectionState.CommunicatingMaster)
                {
                    connectionState = result.state;
                    masterIp = result.masterIp;

                    // Start heartbeat loop (fire and forget)
                    _ = HeartbeatLoop(masterIp);
                }
            }

            if (result.state == ConnectionState.CommunicatingMaster)
            {
                connectionState = result.state;
                masterIp = result.masterIp;

                _ = HeartbeatLoop(masterIp);

                // NEW: start job/data listener
                _ = JobsListenerLoop();
            }

        }
    }

    private static string[] ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    // Escaped quote?
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        sb.Append('"');
                        i++; // skip next quote
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            else
            {
                if (c == '"')
                {
                    inQuotes = true;
                }
                else if (c == ',')
                {
                    fields.Add(sb.ToString());
                    sb.Clear();
                }
                else
                {
                    sb.Append(c);
                }
            }
        }

        fields.Add(sb.ToString());
        return fields.ToArray();
    }

    private static List<CombinedPair> MapChunkFileStreaming(JobSpec job, string filePath, int maxKeysInMemory = 1_000_000)
    {
        var dict = new Dictionary<(string genre, int movieId), (double sum, int count)>();

        using var sr = new StreamReader(filePath);

        while (true)
        {
            var line = sr.ReadLine();
            if (line == null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            var cols = ParseCsvLine(line);
            if (cols.Length < 6) continue;
            if (cols[0].Equals("userId", StringComparison.OrdinalIgnoreCase)) continue;

            if (!int.TryParse(cols[1], out int movieId)) continue;

            if (!double.TryParse(cols[2], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double rating))
                continue;

            if (!long.TryParse(cols[3], out long ts)) continue;

            if (job.FromTimestamp.HasValue && ts < job.FromTimestamp.Value) continue;
            if (job.ToTimestamp.HasValue && ts > job.ToTimestamp.Value) continue;

            var genresRaw = cols[5] ?? "";
            if (string.IsNullOrWhiteSpace(genresRaw)) continue;

            var genres = genresRaw.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var genre in genres)
            {
                if (genre.Equals("(no genres listed)", StringComparison.OrdinalIgnoreCase))
                    continue;

                var key = (genre, movieId);
                if (dict.TryGetValue(key, out var agg))
                    dict[key] = (agg.sum + rating, agg.count + 1);
                else
                    dict[key] = (rating, 1);
            }

            if (dict.Count > maxKeysInMemory)
                throw new Exception($"Map exceeded maxKeysInMemory={maxKeysInMemory}. Use smaller chunks or add spill-to-disk.");
        }

        var res = new List<CombinedPair>(dict.Count);
        foreach (var kv in dict)
        {
            res.Add(new CombinedPair
            {
                Genre = kv.Key.genre,
                MovieId = kv.Key.movieId,
                SumRatings = kv.Value.sum,
                CountRatings = kv.Value.count,
                Title = "" // keep empty to save memory
            });
        }
        return res;
    }

    private static List<GenreTopMovie> ReduceTopN(string jobId, int topN)
    {
        Dictionary<(string genre, int movieId), (double sum, int count)> data;

        lock (_shuffleLock)
        {
            if (!_shuffleStore.TryGetValue(jobId, out var stored))
                return new List<GenreTopMovie>();

            data = new Dictionary<(string genre, int movieId), (double sum, int count)>(stored);
        }

        var perGenre = new Dictionary<string, List<GenreTopMovie>>();

        foreach (var kv in data)
        {
            var genre = kv.Key.genre;
            var movieId = kv.Key.movieId;
            var sum = kv.Value.sum;
            var count = kv.Value.count;
            if (count <= 0) continue;

            var item = new GenreTopMovie
            {
                Genre = genre,
                MovieId = movieId,
                AvgRating = sum / count,
                CountRatings = count
            };

            if (!perGenre.TryGetValue(genre, out var list))
            {
                list = new List<GenreTopMovie>();
                perGenre[genre] = list;
            }
            list.Add(item);
        }

        var result = new List<GenreTopMovie>();
        foreach (var list in perGenre.Values)
        {
            result.AddRange(list
                .OrderByDescending(x => x.AvgRating)
                .ThenByDescending(x => x.CountRatings)
                .ThenBy(x => x.MovieId)
                .Take(topN));
        }

        return result;
    }


    private static List<CombinedPair> MapChunk(JobSpec job, string[] lines)
    {
        // Combiner: (genre,movieId) -> (sum,count,title)
        var dict = new Dictionary<(string genre, int movieId), (double sum, int count, string title)>();

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var cols = ParseCsvLine(line);

            // Expect: userId,movieId,rating,timestamp,title,genres
            if (cols.Length < 6)
                continue;

            // Header safety
            if (cols[0].Equals("userId", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!int.TryParse(cols[1], out int movieId))
                continue;

            if (!double.TryParse(cols[2], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double rating))
                continue;

            if (!long.TryParse(cols[3], out long ts))
                continue;

            // Timestamp filter (optional)
            if (job.FromTimestamp.HasValue && ts < job.FromTimestamp.Value)
                continue;
            if (job.ToTimestamp.HasValue && ts > job.ToTimestamp.Value)
                continue;

            string title = cols[4] ?? "";

            string genresRaw = cols[5] ?? "";
            if (string.IsNullOrWhiteSpace(genresRaw))
                continue;

            var genres = genresRaw.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var genre in genres)
            {
                if (genre.Equals("(no genres listed)", StringComparison.OrdinalIgnoreCase))
                    continue;

                var key = (genre, movieId);

                if (dict.TryGetValue(key, out var agg))
                {
                    dict[key] = (agg.sum + rating, agg.count + 1, string.IsNullOrEmpty(agg.title) ? title : agg.title);
                }
                else
                {
                    dict[key] = (rating, 1, title);
                }
            }
        }

        var results = new List<CombinedPair>(dict.Count);
        foreach (var kv in dict)
        {
            results.Add(new CombinedPair
            {
                Genre = kv.Key.genre,
                MovieId = kv.Key.movieId,
                SumRatings = kv.Value.sum,
                CountRatings = kv.Value.count,
                Title = kv.Value.title
            });
        }

        return results;
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
                HostName = Environment.MachineName,
                NodeId = WorkerNodeId,
                IpAddress = masterIp.ToString(),
                Timestamp = DateTime.UtcNow
            };

            string json = JsonSerializer.Serialize(hb);
            byte[] data = Encoding.UTF8.GetBytes(json);

            await udp.SendAsync(data, data.Length, new IPEndPoint(masterIp, port));
            Logger.Info("Heartbeat sent");

            await Task.Delay(2000); // every 2 seconds
        }
    }




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
        // Canonical: join with '\n' to preserve boundaries deterministically
        var text = string.Join("\n", lines ?? Array.Empty<string>());
        var bytes = Encoding.UTF8.GetBytes(text);
        var hash = sha.ComputeHash(bytes);
        return Convert.ToHexString(hash); // .NET 5+
    }


    private static async Task JobsListenerLoop()
    {
        var listener = new TcpListener(IPAddress.Any, Ports.Jobs);
        listener.Start();
        Logger.Info($"Jobs listener started on TCP {Ports.Jobs}");

        while (true)
        {
            var client = await listener.AcceptTcpClientAsync();

            _ = Task.Run(async () =>
            {
                using (client)
                {
                    using var stream = client.GetStream();
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

                    try
                    {
                        // Read one framed JSON message
                        var frame = await ReadFrameAsync(stream, cts.Token);
                        var json = Encoding.UTF8.GetString(frame);

                        // Determine message type
                        using var doc = JsonDocument.Parse(json);
                        if (!doc.RootElement.TryGetProperty("Type", out var typeProp))
                            throw new Exception("Message has no Type field.");

                        var type = typeProp.GetString();

                        switch (type)
                        {
                            case Shared.Constants.Messages.DataChunkMessageString:
                                {
                                    var msg = JsonSerializer.Deserialize<DataChunkMessage>(json);
                                    if (msg == null)
                                        throw new Exception("Invalid DATA_CHUNK message.");

                                    // Compute hash of received lines exactly
                                    string computed = ComputeSha256OfLines(msg.Lines);

                                    var datasetId = string.IsNullOrWhiteSpace(msg.DatasetId) ? msg.JobId : msg.DatasetId;

                                    var ack = new DataChunkAckMessage
                                    {
                                        JobId = msg.JobId,
                                        DatasetId = datasetId,
                                        ChunkId = msg.ChunkId,
                                        RowCount = msg.Lines?.Length ?? 0,
                                        Sha256 = computed,
                                        Ok = (computed == msg.Sha256) && (msg.RowCount == (msg.Lines?.Length ?? 0)),
                                        Error = ""
                                    };

                                    if (!ack.Ok)
                                        ack.Error = $"Mismatch: expected hash={msg.Sha256}, got={computed}, expected rows={msg.RowCount}, got={ack.RowCount}";

                                    // Store only if OK
                                    if (ack.Ok)
                                    {
                                        var path = GetChunkPath(datasetId, msg.ChunkId);

                                        // Write chunk lines to disk (overwrite per load)
                                        await File.WriteAllLinesAsync(path, msg.Lines ?? Array.Empty<string>(), cts.Token);

                                        lock (_chunkFilesLock)
                                        {
                                            if (!_chunkFiles.TryGetValue(datasetId, out var map))
                                            {
                                                map = new Dictionary<int, string>();
                                                _chunkFiles[datasetId] = map;
                                            }
                                            map[msg.ChunkId] = path;
                                        }

                                        _currentJobId = msg.JobId; // keep if you still use it for error reporting

                                        Logger.Info($"Stored chunk on disk dataset={datasetId} chunk={msg.ChunkId} rows={ack.RowCount} path={path}");
                                    }

                                    else
                                    {
                                        Logger.Warn($"Bad chunk {msg.ChunkId}: {ack.Error}");
                                    }

                                    var ackBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(ack));
                                    await WriteFrameAsync(stream, ackBytes, cts.Token);
                                    break;
                                }

                            case Shared.Constants.Messages.ShufflePartitionMessageString:
                                {
                                    var msg = JsonSerializer.Deserialize<ShufflePartitionMessage>(json);
                                    if (msg == null) throw new Exception("Invalid SHUFFLE_PARTITION.");
                                    if (string.IsNullOrWhiteSpace(msg.JobId)) throw new Exception("SHUFFLE_PARTITION missing JobId.");

                                    int received = 0;

                                    lock (_shuffleLock)
                                    {
                                        if (!_shuffleStore.TryGetValue(msg.JobId, out var store))
                                        {
                                            store = new Dictionary<(string genre, int movieId), (double sum, int count)>();
                                            _shuffleStore[msg.JobId] = store;
                                        }

                                        foreach (var p in msg.Pairs ?? Array.Empty<CombinedPair>())
                                        {
                                            var key = (p.Genre, p.MovieId);
                                            if (store.TryGetValue(key, out var agg))
                                                store[key] = (agg.sum + p.SumRatings, agg.count + p.CountRatings);
                                            else
                                                store[key] = (p.SumRatings, p.CountRatings);

                                            received++;
                                        }
                                    }

                                    var ack = new ShuffleAckMessage
                                    {
                                        Type = Shared.Constants.Messages.ShuffleAckMessageString,
                                        JobId = msg.JobId,
                                        ReducerIndex = msg.ReducerIndex,
                                        ReceivedPairs = received,
                                        Ok = true
                                    };

                                    var ackBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(ack));
                                    await WriteFrameAsync(stream, ackBytes, cts.Token);
                                    break;
                                }
                            case Shared.Constants.Messages.ReduceRequestMessageString:
                                {
                                    var req = JsonSerializer.Deserialize<ReduceRequestMessage>(json);
                                    if (req == null) throw new Exception("Invalid REDUCE_REQUEST.");
                                    if (req.Job == null || string.IsNullOrWhiteSpace(req.Job.JobId)) throw new Exception("REDUCE_REQUEST missing JobId.");

                                    var top = ReduceTopN(req.Job.JobId, req.Job.TopN);

                                    var res = new ReduceResultMessage
                                    {
                                        Type = Shared.Constants.Messages.ReduceResultMessageString,
                                        JobId = req.Job.JobId,
                                        Top = top.ToArray(),
                                        Ok = true
                                    };

                                    var resBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(res));
                                    await WriteFrameAsync(stream, resBytes, cts.Token);
                                    break;
                                }

                            case Shared.Constants.Messages.MapRequestMessageString:
                                {
                                    var req = JsonSerializer.Deserialize<MapRequestMessage>(json);
                                    if (req == null) throw new Exception("Invalid MAP_REQUEST.");
                                    if (string.IsNullOrWhiteSpace(req.DatasetId)) throw new Exception("MAP_REQUEST missing DatasetId.");
                                    if (req.Job == null || string.IsNullOrWhiteSpace(req.Job.JobId)) throw new Exception("MAP_REQUEST missing JobId.");

                                    string path;
                                    lock (_chunkFilesLock)
                                    {
                                        if (!_chunkFiles.TryGetValue(req.DatasetId, out var map) || !map.TryGetValue(req.ChunkId, out path!))
                                            throw new Exception($"Chunk not found on worker. dataset={req.DatasetId} chunk={req.ChunkId}");
                                    }

                                    var pairs = MapChunkFileStreaming(req.Job, path);

                                    var res = new MapResultMessage
                                    {
                                        Type = Shared.Constants.Messages.MapResultMessageString,
                                        JobId = req.Job.JobId,
                                        ChunkId = req.ChunkId,
                                        Pairs = pairs.ToArray(),
                                        Ok = true
                                    };

                                    var resBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(res));
                                    await WriteFrameAsync(stream, resBytes, cts.Token);
                                    break;
                                }
                            case Messages.ShuffleUsersPartitionMessageString:
                                {
                                    var msg = JsonSerializer.Deserialize<ShuffleUsersPartitionMessage>(json);
                                    if (msg == null) throw new Exception("Invalid SHUFFLE_USERS_PARTITION.");
                                    if (string.IsNullOrWhiteSpace(msg.JobId)) throw new Exception("SHUFFLE_USERS_PARTITION missing JobId.");

                                    int received = 0;
                                    lock (_shuffleUsersLock)
                                    {
                                        if (!_shuffleStoreUsers.TryGetValue(msg.JobId, out var store))
                                        {
                                            store = new Dictionary<int, int>();
                                            _shuffleStoreUsers[msg.JobId] = store;
                                        }

                                        foreach (var p in msg.Pairs ?? Array.Empty<UserCountPair>())
                                        {
                                            if (store.TryGetValue(p.UserId, out var c)) store[p.UserId] = c + p.Count;
                                            else store[p.UserId] = p.Count;
                                            received++;
                                        }
                                    }

                                    var ack = new ShuffleUsersAckMessage
                                    {
                                        Type = Messages.ShuffleUsersAckMessageString,
                                        JobId = msg.JobId,
                                        ReducerIndex = msg.ReducerIndex,
                                        ReceivedPairs = received,
                                        Ok = true
                                    };

                                    await WriteFrameAsync(stream, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(ack)), cts.Token);
                                    break;
                                }
                            case Messages.ReduceUsersRequestMessageString:
                                {
                                    var req = JsonSerializer.Deserialize<ReduceUsersRequestMessage>(json);
                                    if (req == null) throw new Exception("Invalid REDUCE_USERS_REQUEST.");
                                    if (req.Job == null || string.IsNullOrWhiteSpace(req.Job.JobId)) throw new Exception("REDUCE_USERS_REQUEST missing JobId.");

                                    var top = ReduceActiveUsers(req.Job.JobId, req.Job.TopN);

                                    var res = new ReduceUsersResultMessage
                                    {
                                        Type = Messages.ReduceUsersResultMessageString,
                                        JobId = req.Job.JobId,
                                        Top = top.ToArray(),
                                        Ok = true
                                    };

                                    await WriteFrameAsync(stream, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(res)), cts.Token);
                                    break;
                                }
                            case Messages.MapUsersRequestMessageString:
                                {
                                    var req = JsonSerializer.Deserialize<MapUsersRequestMessage>(json);
                                    if (req == null) throw new Exception("Invalid MAP_USERS_REQUEST.");
                                    if (string.IsNullOrWhiteSpace(req.DatasetId)) throw new Exception("MAP_USERS_REQUEST missing DatasetId.");
                                    if (req.Job == null || string.IsNullOrWhiteSpace(req.Job.JobId)) throw new Exception("MAP_USERS_REQUEST missing JobId.");

                                    string path;
                                    lock (_chunkFilesLock)
                                    {
                                        if (!_chunkFiles.TryGetValue(req.DatasetId, out var map) || !map.TryGetValue(req.ChunkId, out path!))
                                            throw new Exception($"Chunk not found on worker. dataset={req.DatasetId} chunk={req.ChunkId}");
                                    }

                                    var pairs = MapUsersChunkFileStreaming(req.Job, path);

                                    var res = new MapUsersResultMessage
                                    {
                                        Type = Messages.MapUsersResultMessageString,
                                        JobId = req.Job.JobId,
                                        ChunkId = req.ChunkId,
                                        Pairs = pairs.ToArray(),
                                        Ok = true
                                    };

                                    await WriteFrameAsync(stream, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(res)), cts.Token);
                                    break;
                                }
                            default:
                                throw new Exception($"Unknown message Type: {type}");
                        }
                    }
                    catch (Exception ex)
                    {
                        // If we can, reply with an error in a reasonable format.
                        // Prefer MAP_RESULT if it *looks* like a map request, else DATA_CHUNK_ACK.
                        try
                        {
                            string jsonType = "";
                            try
                            {
                                // best-effort parse
                                using var d = JsonDocument.Parse(Encoding.UTF8.GetString(Array.Empty<byte>()));
                            }
                            catch { /* ignore */ }

                            // We don't actually have the original json here anymore in this catch in a robust way,
                            // so just send a DataChunkAckMessage error (your master already understands it).
                            var ack = new DataChunkAckMessage
                            {
                                JobId = _currentJobId ?? "",
                                ChunkId = -1,
                                Ok = false,
                                Error = ex.Message
                            };

                            var ackBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(ack));
                            await WriteFrameAsync(stream, ackBytes, CancellationToken.None);
                        }
                        catch
                        {
                            // ignore if we can't respond
                        }

                        Logger.Error($"JobsListener error: {ex.Message}");
                    }
                }
            });
        }
    }

    private static string GetChunkPath(string datasetId, int chunkId)
    {
        var baseDir = Path.Combine(AppContext.BaseDirectory, "data", datasetId);
        Directory.CreateDirectory(baseDir);
        return Path.Combine(baseDir, $"chunk_{chunkId}.csvpart");
    }


    private static List<UserCountPair> MapUsersChunkFileStreaming(JobSpec job, string filePath, int maxKeysInMemory = 2_000_000)
    {
        var dict = new Dictionary<int, int>();

        using var sr = new StreamReader(filePath);
        while (true)
        {
            var line = sr.ReadLine();
            if (line == null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            var cols = ParseCsvLine(line);
            if (cols.Length < 6) continue;
            if (cols[0].Equals("userId", StringComparison.OrdinalIgnoreCase)) continue;

            if (!int.TryParse(cols[0], out int userId)) continue;
            if (!long.TryParse(cols[3], out long ts)) continue;

            if (job.FromTimestamp.HasValue && ts < job.FromTimestamp.Value) continue;
            if (job.ToTimestamp.HasValue && ts > job.ToTimestamp.Value) continue;

            if (!string.IsNullOrWhiteSpace(job.GenreFilter))
            {
                var genresRaw = cols[5] ?? "";
                if (string.IsNullOrWhiteSpace(genresRaw)) continue;

                bool match = false;
                foreach (var g in genresRaw.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (g.Equals(job.GenreFilter, StringComparison.OrdinalIgnoreCase))
                    {
                        match = true;
                        break;
                    }
                }
                if (!match) continue;
            }

            if (dict.TryGetValue(userId, out var c)) dict[userId] = c + 1;
            else dict[userId] = 1;

            if (dict.Count > maxKeysInMemory)
                throw new Exception($"ActiveUsers map exceeded maxKeysInMemory={maxKeysInMemory}. Use smaller chunks or spill-to-disk.");
        }

        return dict.Select(kv => new UserCountPair { UserId = kv.Key, Count = kv.Value }).ToList();
    }

    private static List<UserActivity> ReduceActiveUsers(string jobId, int topN)
    {
        Dictionary<int, int> data;
        lock (_shuffleUsersLock)
        {
            if (!_shuffleStoreUsers.TryGetValue(jobId, out var stored))
                return new List<UserActivity>();

            data = new Dictionary<int, int>(stored);
        }

        return data
            .Select(kv => new UserActivity { UserId = kv.Key, Count = kv.Value })
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.UserId)
            .Take(Math.Max(1, topN))
            .ToList();
    }


}
