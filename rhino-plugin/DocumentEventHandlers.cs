using Rhino;
using Rhino.DocObjects;

namespace UrbanBridge.Rhino;

/// <summary>Maps Rhino document changes to incremental bridge messages and road-network rebuilds.</summary>
public sealed class DocumentEventHandlers : IDisposable
{
    private readonly BridgeServer _server;
    private readonly object _debounceLock = new();
    private System.Threading.Timer? _roadNetworkDebounce;
    private const int RoadNetworkDebounceMs = 300;

    public DocumentEventHandlers(BridgeServer server) => _server = server;

    public void Subscribe()
    {
        RhinoDoc.AddRhinoObject += OnObjectAdded;
        RhinoDoc.ReplaceRhinoObject += OnObjectReplaced;
        RhinoDoc.DeleteRhinoObject += OnObjectDeleted;
        RhinoDoc.EndOpenDocument += OnDocumentOpened;
    }

    // RhinoObjectEventArgs does not carry a RhinoDoc in RhinoCommon 8; the event sender is the document.
    private void OnObjectAdded(object? sender, RhinoObjectEventArgs eventArgs)
    {
        if (sender is RhinoDoc document)
        {
            _server.QueueObjectUpsert(document, eventArgs.TheObject);
            MaybeScheduleRoadNetworkRebuild(document, eventArgs.TheObject);
        }
    }

    private void OnObjectReplaced(object? sender, RhinoReplaceObjectEventArgs eventArgs)
    {
        if (sender is RhinoDoc document)
        {
            _server.QueueObjectUpsert(document, eventArgs.NewRhinoObject);
            MaybeScheduleRoadNetworkRebuild(document, eventArgs.NewRhinoObject);
        }
    }

    private void OnObjectDeleted(object? sender, RhinoObjectEventArgs eventArgs)
    {
        _server.SendObjectDeleted(eventArgs.ObjectId);
        // We don't have the object anymore; rebuild if the document is available.
        if (sender is RhinoDoc document)
            ScheduleRoadNetworkRebuild(document);
    }

    private void OnDocumentOpened(object? sender, DocumentOpenEventArgs eventArgs)
    {
        _server.SendFullSync(eventArgs.Document);
        ScheduleRoadNetworkRebuild(eventArgs.Document);
    }

    private void MaybeScheduleRoadNetworkRebuild(RhinoDoc document, RhinoObject rhinoObject)
    {
        var layer = document.Layers[rhinoObject.Attributes.LayerIndex];
        if (layer is null) return;
        var fullPath = layer.FullPath;
        if (fullPath.Equals("Roads", StringComparison.OrdinalIgnoreCase) ||
            fullPath.StartsWith("Roads::", StringComparison.OrdinalIgnoreCase))
        {
            ScheduleRoadNetworkRebuild(document);
        }
    }

    private void ScheduleRoadNetworkRebuild(RhinoDoc document)
    {
        lock (_debounceLock)
        {
            _roadNetworkDebounce?.Dispose();
            _roadNetworkDebounce = new System.Threading.Timer(_ =>
            {
                try
                {
                    RhinoApp.InvokeOnUiThread((Action)(() => _server.RebuildAndSendRoadNetwork(document)));
                }
                catch (Exception ex)
                {
                    RhinoApp.WriteLine($"[UrbanBridge] Road network rebuild failed: {ex.Message}");
                }
            }, null, RoadNetworkDebounceMs, System.Threading.Timeout.Infinite);
        }
    }

    public void Dispose()
    {
        RhinoDoc.AddRhinoObject -= OnObjectAdded;
        RhinoDoc.ReplaceRhinoObject -= OnObjectReplaced;
        RhinoDoc.DeleteRhinoObject -= OnObjectDeleted;
        RhinoDoc.EndOpenDocument -= OnDocumentOpened;
        lock (_debounceLock)
        {
            _roadNetworkDebounce?.Dispose();
            _roadNetworkDebounce = null;
        }
    }
}
