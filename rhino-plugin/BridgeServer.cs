using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Rhino;

namespace UrbanBridge.Rhino;

public sealed partial class BridgeServer : IDisposable
{
    private const int Port = 7890;
    private const string WebSocketMagic = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    private readonly object _clientsLock = new();
    private readonly HashSet<BridgeClient> _clients = new();
    private readonly object _upsertsLock = new();
    private readonly Dictionary<string, ObjectPayload> _pendingUpserts = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly RoadNetworkGraphBuilder _roadBuilder = new();
    private readonly RoadNetworkValidator _roadValidator = new();
    private readonly ZoneAnalysisService _zoneService = new();
    private TcpListener? _listener;
    private Task? _acceptLoop;
    private Timer? _heartbeat;
    private Timer? _flushUpserts;

    public bool IsRunning => _listener is not null;
    public int ConnectedClientCount { get { lock (_clientsLock) return _clients.Count; } }
    public RoadNetworkGraph? LatestRoadNetwork { get; private set; }
    public ZoneAnalysis? LatestZoneAnalysis { get; private set; }
    public double? LatestMassingBuiltGfaSqm { get; set; }
    public DateTime? LastRoadGraphChangeUtc { get; private set; }
    public DateTime? LastRoadSurfaceGenUtc { get; private set; }

    public event Action<RoadNetworkGraph>? RoadNetworkUpdated;
    public event Action<ZoneAnalysis>? ZoneAnalysisUpdated;

    public void Start()
    {
        if (_listener is not null) return;
        _listener = new TcpListener(IPAddress.Loopback, Port);
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
        _heartbeat = new Timer(_ => { try { BroadcastJson(BridgeProtocol.Heartbeat()); } catch { } }, null, 5000, 5000);
        _flushUpserts = new Timer(_ => FlushPendingUpserts(), null, 200, 200);
        RhinoApp.WriteLine($"[UrbanBridge] Bridge listening on ws://localhost:{Port}");
    }

    public void Stop()
    {
        _stopping.Cancel();
        _heartbeat?.Dispose();
        _flushUpserts?.Dispose();
        try { _listener?.Stop(); } catch { }
        _listener = null;
        lock (_clientsLock)
        {
            foreach (var c in _clients) c.Dispose();
            _clients.Clear();
        }
    }

    public void Dispose() => Stop();
    public void NoteRoadGraphDirty() => LastRoadGraphChangeUtc = DateTime.UtcNow;
    public void MarkRoadSurfaceGenerated() => LastRoadSurfaceGenUtc = DateTime.UtcNow;

    public void RebuildAndSendRoadNetwork(RhinoDoc doc)
    {
        if (doc is null) return;
        try
        {
            var graph = _roadBuilder.Build(doc);
            _roadValidator.Validate(doc, graph);
            LatestRoadNetwork = graph;
            LastRoadGraphChangeUtc = DateTime.UtcNow;
            RoadNetworkUpdated?.Invoke(graph);
            BroadcastJson(BridgeProtocol.RoadNetworkUpdate(graph));
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"[UrbanBridge] Road graph rebuild failed: {ex.Message}");
        }
    }

    public void RebuildZoneAnalysis(RhinoDoc doc)
    {
        if (doc is null) return;
        try
        {
            var analysis = _zoneService.Analyze(doc, LastRoadGraphChangeUtc, LastRoadSurfaceGenUtc);
            LatestZoneAnalysis = analysis;
            ZoneAnalysisUpdated?.Invoke(analysis);
            BroadcastJson(BridgeProtocol.ZoneAnalysisUpdate(analysis));
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"[UrbanBridge] Zone analysis failed: {ex.Message}");
        }
    }

    public void QueueObjectUpsert(ObjectPayload payload)
    {
        lock (_upsertsLock)
            _pendingUpserts[payload.Id] = payload;
    }

    private void FlushPendingUpserts()
    {
        List<ObjectPayload> batch;
        lock (_upsertsLock)
        {
            if (_pendingUpserts.Count == 0) return;
            batch = _pendingUpserts.Values.ToList();
            _pendingUpserts.Clear();
        }
        try { BroadcastJson(BridgeProtocol.ObjectsUpsert(batch)); }
        catch { }
    }

    public void BroadcastJson(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        lock (_clientsLock)
        {
            foreach (var c in _clients.ToList())
            {
                try { c.SendText(bytes); }
                catch { _clients.Remove(c); c.Dispose(); }
            }
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync();
                _ = Task.Run(() => HandleClientAsync(client));
            }
            catch (ObjectDisposedException) { break; }
            catch { if (_stopping.IsCancellationRequested) break; }
        }
    }

    private async Task HandleClientAsync(TcpClient tcp)
    {
        BridgeClient? bridge = null;
        try
        {
            var stream = tcp.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, true);
            var requestLine = await reader.ReadLineAsync();
            if (requestLine is null || !requestLine.StartsWith("GET ", StringComparison.Ordinal)) return;
            string? key = null;
            while (true)
            {
                var line = await reader.ReadLineAsync();
                if (string.IsNullOrEmpty(line)) break;
                if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
                    key = line.Substring(18).Trim();
            }
            if (key is null) return;
            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + WebSocketMagic)));
            var response = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
            bridge = new BridgeClient(tcp, stream);
            lock (_clientsLock) _clients.Add(bridge);
            if (LatestRoadNetwork is { } g)
                bridge.SendText(Encoding.UTF8.GetBytes(BridgeProtocol.RoadNetworkUpdate(g)));
            if (LatestZoneAnalysis is { } z)
                bridge.SendText(Encoding.UTF8.GetBytes(BridgeProtocol.ZoneAnalysisUpdate(z)));
            await bridge.ReceiveLoopAsync(_stopping.Token);
        }
        catch { }
        finally
        {
            if (bridge is not null)
            {
                lock (_clientsLock) _clients.Remove(bridge);
                bridge.Dispose();
            }
            else try { tcp.Close(); } catch { }
        }
    }
}

internal sealed class BridgeClient : IDisposable
{
    private readonly TcpClient _tcpClient;
    private readonly NetworkStream _stream;
    private readonly object _sendLock = new();

    public BridgeClient(TcpClient tcp, NetworkStream stream)
    {
        _tcpClient = tcp;
        _stream = stream;
    }

    public void SendText(byte[] payload)
    {
        lock (_sendLock)
        {
            var frame = BuildTextFrame(payload);
            _stream.Write(frame, 0, frame.Length);
        }
    }

    public async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[4096];
        while (!ct.IsCancellationRequested)
        {
            var n = await _stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            if (n == 0) break;
        }
    }

    private static byte[] BuildTextFrame(byte[] payload)
    {
        var len = payload.Length;
        byte[] header;
        if (len < 126) header = new byte[] { 0x81, (byte)len };
        else if (len < 65536) header = new byte[] { 0x81, 126, (byte)(len >> 8), (byte)len };
        else header = new byte[] { 0x81, 127, 0, 0, 0, 0, (byte)(len >> 24), (byte)(len >> 16), (byte)(len >> 8), (byte)len };
        var frame = new byte[header.Length + payload.Length];
        Buffer.BlockCopy(header, 0, frame, 0, header.Length);
        Buffer.BlockCopy(payload, 0, frame, header.Length, payload.Length);
        return frame;
    }

    public void Dispose()
    {
        try { _stream.Dispose(); } catch { }
        try { _tcpClient.Dispose(); } catch { }
    }
}
