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
    private static readonly Dictionary<int, string[]> _receivedChunks = new();
    private static readonly object _dataLock = new();
    private static volatile string? _currentJobId = null;


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

                                var ack = new DataChunkAckMessage
                                {
                                    JobId = msg.JobId,
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
                                    lock (_dataLock)
                                    {
                                        _currentJobId = msg.JobId;
                                        _receivedChunks[msg.ChunkId] = msg.Lines;
                                    }

                                    Logger.Info($"Received chunk {msg.ChunkId}/{msg.TotalChunks} rows={ack.RowCount}");
                                }
                                else
                                {
                                    Logger.Warn($"Bad chunk {msg.ChunkId}: {ack.Error}");
                                }

                                var ackBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(ack));
                                await WriteFrameAsync(stream, ackBytes, cts.Token);
                                break;
                            }

                            case Shared.Constants.Messages.MapRequestMessageString:
                            {
                                var req = JsonSerializer.Deserialize<MapRequestMessage>(json);
                                if (req == null)
                                    throw new Exception("Invalid MAP_REQUEST message.");
                                if (req.Job == null || string.IsNullOrWhiteSpace(req.Job.JobId))
                                    throw new Exception("MAP_REQUEST missing Job.JobId.");

                                // Grab the chunk lines we previously stored from DATA_CHUNK
                                string[] lines;
                                lock (_dataLock)
                                {
                                    if (!_receivedChunks.TryGetValue(req.ChunkId, out lines!))
                                        throw new Exception($"Chunk {req.ChunkId} not found on worker.");
                                }

                                // Run MAP (combiner)
                                var pairs = MapChunk(req.Job, lines);

                                var res = new MapResultMessage
                                {
                                    JobId = req.Job.JobId,
                                    ChunkId = req.ChunkId,
                                    Pairs = pairs.ToArray(),
                                    Ok = true,
                                    Error = ""
                                };

                                Logger.Info($"MAP done job={res.JobId} chunk={res.ChunkId} pairs={res.Pairs.Length}");

                                var resBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(res));
                                await WriteFrameAsync(stream, resBytes, cts.Token);
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




}
