using Rhino;
using Rhino.DocObjects;
using Rhino.Commands;

namespace UrbanBridge.Plugin;

/// <summary>Document events → bridge upserts + road/zone dirty flags.</summary>
public sealed class DocumentEventHandlers
{
    private readonly BridgeServer _server;
    private System.Threading.Timer? _roadDebounce;
    private System.Threading.Timer? _zoneDebounce;
    private readonly object _debounceLock = new();

    public DocumentEventHandlers(BridgeServer server)
    {
        _server = server;
    }

    public void Register()
    {
        RhinoDoc.AddRhinoObject += OnAdd;
        RhinoDoc.DeleteRhinoObject += OnDelete;
        RhinoDoc.ReplaceRhinoObject += OnReplace;
        RhinoDoc.ModifyObjectAttributes += OnModifyAttributes;
        RhinoDoc.EndOpenDocument += OnEndOpen;
    }

    public void Unregister()
    {
        RhinoDoc.AddRhinoObject -= OnAdd;
        RhinoDoc.DeleteRhinoObject -= OnDelete;
        RhinoDoc.ReplaceRhinoObject -= OnReplace;
        RhinoDoc.ModifyObjectAttributes -= OnModifyAttributes;
        RhinoDoc.EndOpenDocument -= OnEndOpen;
        lock (_debounceLock)
        {
            _roadDebounce?.Dispose();
            _zoneDebounce?.Dispose();
        }
    }

    private void OnEndOpen(object? sender, DocumentOpenEventArgs e)
    {
        if (e.Document is null) return;
        ScheduleRoadRebuild(e.Document);
        ScheduleZoneRebuild(e.Document);
    }

    private void OnAdd(object? sender, RhinoObjectEventArgs e) => HandleObject(e.TheObject);
    private void OnDelete(object? sender, RhinoObjectEventArgs e)
    {
        if (e.TheObject is null) return;
        _server.BroadcastJson(BridgeProtocol.ObjectDeleted(e.TheObject.Id));
        NoteLayers(e.TheObject);
    }

    private void OnReplace(object? sender, RhinoReplaceObjectEventArgs e)
    {
        if (e.NewRhinoObject is not null) HandleObject(e.NewRhinoObject);
    }

    private void OnModifyAttributes(object? sender, RhinoModifyObjectAttributesEventArgs e)
    {
        if (e.RhinoObject is not null) HandleObject(e.RhinoObject);
    }

    private void HandleObject(RhinoObject? obj)
    {
        if (obj is null || obj.IsDeleted) return;
        var doc = obj.Document ?? RhinoDoc.ActiveDoc;
        if (doc is null) return;

        if (ObjectSerializer.TrySerialize(doc, obj, out var payload) && payload is not null)
            _server.QueueObjectUpsert(payload);

        NoteLayers(obj);
    }

    private void NoteLayers(RhinoObject obj)
    {
        var doc = obj.Document ?? RhinoDoc.ActiveDoc;
        if (doc is null) return;
        var layer = doc.Layers[obj.Attributes.LayerIndex];
        if (layer is null) return;
        var path = layer.FullPath;
        if (path.StartsWith("Roads", StringComparison.OrdinalIgnoreCase))
        {
            _server.NoteRoadGraphDirty();
            ScheduleRoadRebuild(doc);
        }
        if (path.StartsWith("Zones", StringComparison.OrdinalIgnoreCase))
            ScheduleZoneRebuild(doc);
    }

    private void ScheduleRoadRebuild(RhinoDoc doc)
    {
        lock (_debounceLock)
        {
            _roadDebounce?.Dispose();
            _roadDebounce = new System.Threading.Timer(_ =>
            {
                try { _server.RebuildAndSendRoadNetwork(doc); }
                catch (Exception ex) { RhinoApp.WriteLine($"[UrbanBridge] Road debounce: {ex.Message}"); }
            }, null, 400, System.Threading.Timeout.Infinite);
        }
    }

    private void ScheduleZoneRebuild(RhinoDoc doc)
    {
        lock (_debounceLock)
        {
            _zoneDebounce?.Dispose();
            _zoneDebounce = new System.Threading.Timer(_ =>
            {
                try { _server.RebuildZoneAnalysis(doc); }
                catch (Exception ex) { RhinoApp.WriteLine($"[UrbanBridge] Zone debounce: {ex.Message}"); }
            }, null, 400, System.Threading.Timeout.Infinite);
        }
    }
}
