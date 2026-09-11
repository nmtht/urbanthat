using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Rhino;

namespace UrbanBridge.Rhino;

/// <summary>Local WebSocket host. It accepts one or more Unreal clients and broadcasts Rhino events.</summary>
public sealed class BridgeServer : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _clientsLock = new();
    private readonly HashSet<WebSocket> _clients = [];
    private readonly object _upsertsLock = new();
    private readonly Dictionary<Guid, ObjectPayload> _pendingUpserts = [];
    private Task? _acceptLoop;
    private Timer? _heartbeat;
    private Timer? _upsertTimer;

    public void Start()
    {
        _listener.Prefixes.Add("http://localhost:7890/");
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
        _heartbeat = new Timer(_ => _ = BroadcastAsync(new BridgeMessage("heartbeat", Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())), null,
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    /// <summary>Coalesces changes occurring in the same short Rhino operation into batch_upsert.</summary>
    public void QueueObjectUpsert(RhinoDoc document, Rhino.DocObjects.RhinoObject rhinoObject)
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
        _ = upserts.Count == 1
            ? BroadcastAsync(new BridgeMessage("object_upserted", Object: upserts[0]))
            : BroadcastAsync(new BridgeMessage("batch_upsert", Objects: upserts));
    }

    public void SendFullSync(RhinoDoc document)
    {
        var objects = new List<object>();
        foreach (var rhinoObject in document.Objects)
        {
            if (rhinoObject.IsDeleted || !rhinoObject.IsVisible) continue;
            if (ObjectSerializer.TrySerialize(document, rhinoObject, out var payload) && payload is not null) objects.Add(payload);
        }
        _ = BroadcastAsync(new BridgeMessage("full_sync", Objects: objects, DocumentId: document.Id.ToString(), Units: "meters"));
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                if (!context.Request.IsWebSocketRequest)
                {
                    context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                    context.Response.Close();
                    continue;
                }
                var socketContext = await context.AcceptWebSocketAsync(null);
                lock (_clientsLock) _clients.Add(socketContext.WebSocket);
                RhinoApp.WriteLine("[UrbanBridge] Unreal client connected.");
                _ = ReceiveLoopAsync(socketContext.WebSocket);
            }
            catch (HttpListenerException) when (_stopping.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (_stopping.IsCancellationRequested) { }
            catch (Exception exception)
            {
                RhinoApp.WriteLine($"[UrbanBridge] WebSocket accept error: {exception.Message}");
            }
        }
    }

    private async Task ReceiveLoopAsync(WebSocket socket)
    {
        var buffer = new byte[4096];
        try
        {
            while (socket.State == WebSocketState.Open && !_stopping.IsCancellationRequested)
            {
                var received = await socket.ReceiveAsync(buffer, _stopping.Token);
                if (received.MessageType == WebSocketMessageType.Close) break;
                var json = Encoding.UTF8.GetString(buffer, 0, received.Count);
                using var message = JsonDocument.Parse(json);
                if (message.RootElement.TryGetProperty("type", out var type) && type.GetString() == "request_full_sync")
                    RhinoApp.InvokeOnUiThread((Action)(() => SendFullSync(RhinoDoc.ActiveDoc)));
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (JsonException exception) { RhinoApp.WriteLine($"[UrbanBridge] Invalid client JSON: {exception.Message}"); }
        finally
        {
            lock (_clientsLock) _clients.Remove(socket);
            socket.Dispose();
            RhinoApp.WriteLine("[UrbanBridge] Unreal client disconnected.");
        }
    }

    private async Task BroadcastAsync(BridgeMessage message)
    {
        var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, JsonOptions));
        WebSocket[] clients;
        lock (_clientsLock) clients = _clients.Where(socket => socket.State == WebSocketState.Open).ToArray();
        foreach (var client in clients)
        {
            try { await client.SendAsync(payload, WebSocketMessageType.Text, true, _stopping.Token); }
            catch (WebSocketException) { lock (_clientsLock) _clients.Remove(client); }
            catch (OperationCanceledException) { }
        }
    }

    public void Dispose()
    {
        _heartbeat?.Dispose();
        _upsertTimer?.Dispose();
        _stopping.Cancel();
        _listener.Close();
        lock (_clientsLock)
        {
            foreach (var client in _clients) client.Abort();
            _clients.Clear();
        }
        try { _acceptLoop?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _stopping.Dispose();
    }
}
