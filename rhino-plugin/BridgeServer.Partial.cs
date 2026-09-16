using Rhino;
using Rhino.DocObjects;

namespace UrbanBridge.Rhino;

public sealed partial class BridgeServer
{
    public int ClientCount => ConnectedClientCount;

    public void MarkRoadGraphChanged() => NoteRoadGraphDirty();

    public void NotifyRoadNetworkUpdated(RoadNetworkGraph graph)
    {
        LatestRoadNetwork = graph;
        RoadNetworkUpdated?.Invoke(graph);
        try { BroadcastJson(BridgeProtocol.RoadNetworkUpdate(graph)); }
        catch { }
    }

    public void QueueObjectUpsert(RhinoDoc document, RhinoObject rhinoObject)
    {
        if (document is null || rhinoObject is null) return;
        try
        {
            if (ObjectSerializer.TrySerialize(document, rhinoObject, out var payload) && payload is not null)
                QueueObjectUpsert(payload);
        }
        catch { }
    }

    public void SendObjectDeleted(Guid objectId)
    {
        try { BroadcastJson(BridgeProtocol.ObjectDeleted(objectId)); }
        catch { }
    }

    public void SendFullSync(RhinoDoc document)
    {
        if (document is null) return;
        try
        {
            RebuildAndSendRoadNetwork(document);
            RebuildZoneAnalysis(document);
            var objects = new List<ObjectPayload>();
            foreach (var obj in document.Objects)
            {
                if (obj is null || obj.IsDeleted) continue;
                try
                {
                    if (ObjectSerializer.TrySerialize(document, obj, out var p) && p is not null)
                        objects.Add(p);
                }
                catch { }
            }
            if (objects.Count > 0)
                BroadcastJson(BridgeProtocol.ObjectsUpsert(objects));
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"[UrbanBridge] Full sync failed: {ex.Message}");
        }
    }
}
