using System.Text.Json;
using System.Text.Json.Serialization;
using Fleck;
using Rhino;

namespace UrbanBridge.Rhino;

/// <summary>Cross-platform local WebSocket host that broadcasts Rhino document changes.</summary>
public sealed class BridgeServer : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private readonly object _clientsLock = new();
    private readonly HashSet<IWebSocketConnection> _clients = [];
    private readonly object _upsertsLock = new();
    private readonly Dictionary<Guid, ObjectPayload> _pendingUpserts = [];
    private WebSocketServer? _server;
    private Timer? _heartbeat;
    private Timer? _upsertTimer;

    public bool IsRunning => _server is not null;
    public int ClientCount { get { lock (_clientsLock) return _clients.Count; } }

    public void Start()
    {
        // Fleck is .NET Standard and runs in both Rhino 8 for Windows and macOS.
        _server = new WebSocketServer("ws://127.0.0.1:7890");
        _server.Start(socket =>
        {
            socket.OnOpen = () => OnClientOpened(socket);
            socket.OnClose = () => OnClientClosed(socket);
            socket.OnError = exception => RhinoApp.WriteLine($"[UrbanBridge] WebSocket client error: {exception.Message}");
            socket.OnMessage = message => OnClientMessage(message);
        });
        _heartbeat = new Timer(_ => Broadcast(new BridgeMessage("heartbeat", Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())), null,
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
        Broadcast(new BridgeMessage("object_deleted", Id: objectId.ToString()));
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
        Broadcast(upserts.Count == 1
            ? new BridgeMessage("object_upserted", Object: upserts[0])
            : new BridgeMessage("batch_upsert", Objects: upserts));
    }

    public void SendFullSync(RhinoDoc document)
    {
        var objects = new List<object>();
        foreach (var rhinoObject in document.Objects)
        {
            if (rhinoObject.IsDeleted || !rhinoObject.IsVisible) continue;
            if (ObjectSerializer.TrySerialize(document, rhinoObject, out var payload) && payload is not null) objects.Add(payload);
        }
        Broadcast(new BridgeMessage("full_sync", Objects: objects, DocumentId: document.Id.ToString(), Units: "meters"));
    }

    private void OnClientOpened(IWebSocketConnection socket)
    {
        lock (_clientsLock) _clients.Add(socket);
        RhinoApp.WriteLine("[UrbanBridge] Unreal client connected.");
    }

    private void OnClientClosed(IWebSocketConnection socket)
    {
        lock (_clientsLock) _clients.Remove(socket);
        RhinoApp.WriteLine("[UrbanBridge] Unreal client disconnected.");
    }

    private void OnClientMessage(string json)
    {
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

    private void Broadcast(BridgeMessage message)
    {
        var payload = JsonSerializer.Serialize(message, JsonOptions);
        IWebSocketConnection[] clients;
        lock (_clientsLock) clients = _clients.ToArray();
        foreach (var client in clients)
        {
            try { client.Send(payload); }
            catch (Exception exception)
            {
                lock (_clientsLock) _clients.Remove(client);
                RhinoApp.WriteLine($"[UrbanBridge] WebSocket send error: {exception.Message}");
            }
        }
    }

    public void Dispose()
    {
        _heartbeat?.Dispose();
        _upsertTimer?.Dispose();
        lock (_clientsLock)
        {
            foreach (var client in _clients) client.Close();
            _clients.Clear();
        }
        _server?.Dispose();
        _server = null;
    }
}
