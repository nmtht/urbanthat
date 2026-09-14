using Rhino;
using Rhino.DocObjects;

namespace UrbanBridge.Rhino;

/// <summary>Maps Rhino document changes to bridge messages, road graph, and zone analysis rebuilds.</summary>
public sealed class DocumentEventHandlers : IDisposable
{
    private readonly BridgeServer _server;
    private readonly object _debounceLock = new();
    private System.Threading.Timer? _roadNetworkDebounce;
    private System.Threading.Timer? _zoneDebounce;
    private const int DebounceMs = 300;

    public DocumentEventHandlers(BridgeServer server) => _server = server;

    public void Subscribe()
    {
        RhinoDoc.AddRhinoObject += OnObjectAdded;
        RhinoDoc.ReplaceRhinoObject += OnObjectReplaced;
        RhinoDoc.DeleteRhinoObject += OnObjectDeleted;
        RhinoDoc.EndOpenDocument += OnDocumentOpened;
    }

    private void OnObjectAdded(object? sender, RhinoObjectEventArgs eventArgs)
    {
        if (sender is RhinoDoc document)
        {
            _server.QueueObjectUpsert(document, eventArgs.TheObject);
            MaybeScheduleRoad(document, eventArgs.TheObject);
            MaybeScheduleZones(document, eventArgs.TheObject);
        }
    }

    private void OnObjectReplaced(object? sender, RhinoReplaceObjectEventArgs eventArgs)
    {
        if (sender is RhinoDoc document)
        {
            _server.QueueObjectUpsert(document, eventArgs.NewRhinoObject);
            MaybeScheduleRoad(document, eventArgs.NewRhinoObject);
            MaybeScheduleZones(document, eventArgs.NewRhinoObject);
        }
    }

    private void OnObjectDeleted(object? sender, RhinoObjectEventArgs eventArgs)
    {
        _server.SendObjectDeleted(eventArgs.ObjectId);
        if (sender is RhinoDoc document)
        {
            ScheduleRoadNetworkRebuild(document);
            ScheduleZoneRebuild(document);
        }
    }

    private void OnDocumentOpened(object? sender, DocumentOpenEventArgs eventArgs)
    {
        _server.SendFullSync(eventArgs.Document);
        ScheduleRoadNetworkRebuild(eventArgs.Document);
        ScheduleZoneRebuild(eventArgs.Document);
    }

    private static string? LayerPath(RhinoDoc document, RhinoObject rhinoObject)
    {
        var layer = document.Layers[rhinoObject.Attributes.LayerIndex];
        return layer?.FullPath;
    }

    private void MaybeScheduleRoad(RhinoDoc document, RhinoObject rhinoObject)
    {
        var fullPath = LayerPath(document, rhinoObject);
        if (fullPath is null) return;
        if (fullPath.Equals("Roads", StringComparison.OrdinalIgnoreCase) ||
            fullPath.StartsWith("Roads::", StringComparison.OrdinalIgnoreCase))
        {
            ScheduleRoadNetworkRebuild(document);
        }
    }

    private void MaybeScheduleZones(RhinoDoc document, RhinoObject rhinoObject)
    {
        var fullPath = LayerPath(document, rhinoObject);
        if (fullPath is null) return;
        if (fullPath.Equals("Zones", StringComparison.OrdinalIgnoreCase) ||
            fullPath.StartsWith("Zones::", StringComparison.OrdinalIgnoreCase))
        {
            ScheduleZoneRebuild(document);
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
                    RhinoApp.InvokeOnUiThread((Action)(() =>
                    {
                        _server.MarkRoadGraphChanged();
                        _server.RebuildAndSendRoadNetwork(document);
                        // Road access for zones depends on surfaces; re-run zone analysis for stale flag
                        _server.RebuildZoneAnalysis(document);
                    }));
                }
                catch (Exception ex)
                {
                    RhinoApp.WriteLine($"[UrbanBridge] Road network rebuild failed: {ex.Message}");
                }
            }, null, DebounceMs, System.Threading.Timeout.Infinite);
        }
    }

    private void ScheduleZoneRebuild(RhinoDoc document)
    {
        lock (_debounceLock)
        {
            _zoneDebounce?.Dispose();
            _zoneDebounce = new System.Threading.Timer(_ =>
            {
                try
                {
                    RhinoApp.InvokeOnUiThread((Action)(() => _server.RebuildZoneAnalysis(document)));
                }
                catch (Exception ex)
                {
                    RhinoApp.WriteLine($"[UrbanBridge] Zone analysis failed: {ex.Message}");
                }
            }, null, DebounceMs, System.Threading.Timeout.Infinite);
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
            _zoneDebounce?.Dispose();
            _zoneDebounce = null;
        }
    }
}
