using Rhino;
using Rhino.DocObjects;

namespace UrbanBridge.Rhino;

/// <summary>Maps Rhino document changes to incremental bridge messages.</summary>
public sealed class DocumentEventHandlers : IDisposable
{
    private readonly BridgeServer _server;

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
        if (sender is RhinoDoc document) _server.QueueObjectUpsert(document, eventArgs.TheObject);
    }

    private void OnObjectReplaced(object? sender, RhinoReplaceObjectEventArgs eventArgs)
    {
        if (sender is RhinoDoc document) _server.QueueObjectUpsert(document, eventArgs.NewRhinoObject);
    }

    private void OnObjectDeleted(object? sender, RhinoObjectEventArgs eventArgs) => _server.SendObjectDeleted(eventArgs.ObjectId);
    private void OnDocumentOpened(object? sender, DocumentOpenEventArgs eventArgs) => _server.SendFullSync(eventArgs.Document);

    public void Dispose()
    {
        RhinoDoc.AddRhinoObject -= OnObjectAdded;
        RhinoDoc.ReplaceRhinoObject -= OnObjectReplaced;
        RhinoDoc.DeleteRhinoObject -= OnObjectDeleted;
        RhinoDoc.EndOpenDocument -= OnDocumentOpened;
    }
}
