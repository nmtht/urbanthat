using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Rhino;

namespace UrbanBridge.Rhino;

/// <summary>
/// Dependency-free, loopback-only WebSocket server. Keeping it in the .rhp makes
/// installation through Rhino's PlugInManager a single-file operation.
/// </summary>
public sealed class BridgeServer : IDisposable
{
    private const int Port = 7890;
    private const string WebSocketMagic = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    private static readonly JsonSerializerOptions JsonOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private readonly object _clientsLock = new();
    private readonly HashSet<BridgeClient> _clients = new();
    private readonly object _upsertsLock = new();
    private readonly Dictionary<Guid, ObjectPayload> _pendingUpserts = new();
    private readonly CancellationTokenSource _stopping = new();
    private TcpListener? _listener;
    private Task? _acceptLoop;
    private Timer? _heartbeat;
    private Timer? _upsertTimer;

    public bool IsRunning => _listener is not null;
    public int ClientCount { get { lock (_clientsLock) return _clients.Count; } }

    public void Start()
    {
        _listener = new TcpListener(IPAddress.Loopback, Port);
        _listener.Start();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_stopping.Token));
        _heartbeat = new Timer(_ => _ = BroadcastAsync(new BridgeMessage("heartbeat", Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())), null,
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    /// <summary>Coalesces changes occurring in the same short Rhino operation into batch_upsert.</summary>
    public void QueueObjectUpsert(RhinoDoc document, global::Rhino.DocObjects.RhinoObject rhinoObject)
    {
        if (!ObjectSerializer.TrySerialize(document, rhinoObject, out var payload) || payload is null) return;
        lock (_upsertsLock)
        {
            _pendingUpserts[rhinoObject.Id] = payload;
            _upsertTimer ??= new Timer(_ => FlushUpserts(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _upsertTimer.Change(TimeSpan.FromMilliseconds(75), Timeout.InfiniteTimeSpan);
        }
    }

    public void SendObjectDeleted(Guid objectId)
    {
        lock (_upsertsLock) _pendingUpserts.Remove(objectId);
        _ = BroadcastAsync(new BridgeMessage("object_deleted", Id: objectId.ToString()));
    }

    private void FlushUpserts()
    {
        List<object> upserts;
        lock (_upsertsLock)
        {
            if (_pendingUpserts.Count == 0) return;
            upserts = _pendingUpserts.Values.Cast<object>().ToList();
            _pendingUpserts.Clear();
        }
        _ = BroadcastAsync(upserts.Count == 1
            ? new BridgeMessage("object_upserted", Object: upserts[0])
            : new BridgeMessage("batch_upsert", Objects: upserts));
    }

    public void SendFullSync(RhinoDoc document)
    {
        var objects = new List<object>();
        foreach (var rhinoObject in document.Objects)
        {
            if (rhinoObject.IsDeleted) continue;
            if (ObjectSerializer.TrySerialize(document, rhinoObject, out var payload) && payload is not null) objects.Add(payload);
        }
        _ = BroadcastAsync(new BridgeMessage("full_sync", Objects: objects, DocumentId: document.RuntimeSerialNumber.ToString(), Units: "meters"));
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var tcpClient = await _listener!.AcceptTcpClientAsync(cancellationToken);
                _ = Task.Run(() => AcceptClientAsync(tcpClient, cancellationToken), cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            RhinoApp.WriteLine($"[UrbanBridge] WebSocket accept error: {exception.Message}");
        }
    }

    private async Task AcceptClientAsync(TcpClient tcpClient, CancellationToken cancellationToken)
    {
        var client = new BridgeClient(tcpClient);
        try
        {
            await client.AcceptHandshakeAsync(cancellationToken);
            lock (_clientsLock) _clients.Add(client);
            RhinoApp.WriteLine("[UrbanBridge] Unreal client connected.");
            await ReceiveLoopAsync(client, cancellationToken);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            RhinoApp.WriteLine($"[UrbanBridge] WebSocket client error: {exception.Message}");
        }
        finally
        {
            lock (_clientsLock) _clients.Remove(client);
            client.Dispose();
            RhinoApp.WriteLine("[UrbanBridge] Unreal client disconnected.");
        }
    }

    private async Task ReceiveLoopAsync(BridgeClient client, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var json = await client.ReceiveTextAsync(cancellationToken);
            if (json is null) return;
            try
            {
                using var message = JsonDocument.Parse(json);
                if (message.RootElement.TryGetProperty("type", out var type) && type.GetString() == "request_full_sync")
                    RhinoApp.InvokeOnUiThread((Action)(() =>
                    {
                        if (RhinoDoc.ActiveDoc is { } document) SendFullSync(document);
                    }));
            }
            catch (JsonException exception)
            {
                RhinoApp.WriteLine($"[UrbanBridge] Invalid client JSON: {exception.Message}");
            }
        }
    }

    private async Task BroadcastAsync(BridgeMessage message)
    {
        var payload = JsonSerializer.Serialize(message, JsonOptions);
        BridgeClient[] clients;
        lock (_clientsLock) clients = _clients.ToArray();
        foreach (var client in clients)
        {
            try { await client.SendTextAsync(payload, _stopping.Token); }
            catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException)
            {
                lock (_clientsLock) _clients.Remove(client);
                client.Dispose();
            }
        }
    }

    public void Dispose()
    {
        _heartbeat?.Dispose();
        _upsertTimer?.Dispose();
        _stopping.Cancel();
        _listener?.Stop();
        lock (_clientsLock)
        {
            foreach (var client in _clients) client.Dispose();
            _clients.Clear();
        }
        try { _acceptLoop?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _stopping.Dispose();
        _listener = null;
    }

    private sealed class BridgeClient : IDisposable
    {
        private readonly TcpClient _tcpClient;
        private readonly NetworkStream _stream;
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        public BridgeClient(TcpClient tcpClient)
        {
            _tcpClient = tcpClient;
            _stream = tcpClient.GetStream();
        }

        public async Task AcceptHandshakeAsync(CancellationToken cancellationToken)
        {
            var request = await ReadHttpHeadersAsync(cancellationToken);
            var keyLine = request.Split("\r\n")
                .FirstOrDefault(line => line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase));
            var separator = keyLine?.IndexOf(':') ?? -1;
            var key = separator >= 0 ? keyLine![(separator + 1)..].Trim() : null;
            if (string.IsNullOrWhiteSpace(key)) throw new InvalidDataException("Missing Sec-WebSocket-Key.");
            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + WebSocketMagic)));
            var response = $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n";
            await _stream.WriteAsync(Encoding.ASCII.GetBytes(response), cancellationToken);
        }

        public async Task<string?> ReceiveTextAsync(CancellationToken cancellationToken)
        {
            var header = new byte[2];
            if (!await ReadExactlyAsync(header, cancellationToken)) return null;
            var opcode = header[0] & 0x0F;
            if (opcode == 0x8) return null;
            if (opcode != 0x1) throw new InvalidDataException("Only text WebSocket frames are supported.");
            var masked = (header[1] & 0x80) != 0;
            if (!masked) throw new InvalidDataException("Client WebSocket frames must be masked.");
            ulong length = (uint)(header[1] & 0x7F);
            if (length == 126)
            {
                var extended = new byte[2];
                await ReadRequiredAsync(extended, cancellationToken);
                length = (uint)((extended[0] << 8) | extended[1]);
            }
            else if (length == 127)
            {
                var extended = new byte[8];
                await ReadRequiredAsync(extended, cancellationToken);
                length = 0;
                foreach (var value in extended) length = (length << 8) | value;
            }
            if (length > 1_048_576) throw new InvalidDataException("WebSocket message is too large.");
            var mask = new byte[4];
            await ReadRequiredAsync(mask, cancellationToken);
            var payload = new byte[(int)length];
            await ReadRequiredAsync(payload, cancellationToken);
            for (var index = 0; index < payload.Length; index++) payload[index] ^= mask[index % 4];
            return Encoding.UTF8.GetString(payload);
        }

        public async Task SendTextAsync(string text, CancellationToken cancellationToken)
        {
            var payload = Encoding.UTF8.GetBytes(text);
            await _sendLock.WaitAsync(cancellationToken);
            try
            {
                var header = CreateTextFrameHeader(payload.Length);
                await _stream.WriteAsync(header, cancellationToken);
                await _stream.WriteAsync(payload, cancellationToken);
            }
            finally { _sendLock.Release(); }
        }

        private static byte[] CreateTextFrameHeader(int length)
        {
            if (length <= 125) return new byte[] { 0x81, (byte)length };
            if (length <= ushort.MaxValue) return new byte[] { 0x81, 126, (byte)(length >> 8), (byte)length };
            var header = new byte[10] { 0x81, 127, 0, 0, 0, 0, 0, 0, 0, 0 };
            var value = (ulong)length;
            for (var index = 9; index >= 2; index--)
            {
                header[index] = (byte)value;
                value >>= 8;
            }
            return header;
        }

        private async Task<string> ReadHttpHeadersAsync(CancellationToken cancellationToken)
        {
            var buffer = new List<byte>(1024);
            while (buffer.Count < 16_384)
            {
                var value = new byte[1];
                await ReadRequiredAsync(value, cancellationToken);
                buffer.Add(value[0]);
                if (buffer.Count >= 4 && buffer[^4] == '\r' && buffer[^3] == '\n' && buffer[^2] == '\r' && buffer[^1] == '\n')
                    return Encoding.ASCII.GetString(buffer.ToArray());
            }
            throw new InvalidDataException("WebSocket handshake is too large.");
        }

        private async Task ReadRequiredAsync(byte[] buffer, CancellationToken cancellationToken)
        {
            if (!await ReadExactlyAsync(buffer, cancellationToken)) throw new EndOfStreamException();
        }

        private async Task<bool> ReadExactlyAsync(byte[] buffer, CancellationToken cancellationToken)
        {
            var read = 0;
            while (read < buffer.Length)
            {
                var count = await _stream.ReadAsync(buffer.AsMemory(read), cancellationToken);
                if (count == 0) return false;
                read += count;
            }
            return true;
        }

        public void Dispose()
        {
            _stream.Dispose();
            _tcpClient.Dispose();
            _sendLock.Dispose();
        }
    }
}
