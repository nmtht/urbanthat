using Rhino;

namespace UrbanBridge.Plugin;

/// <summary>Compatibility shims for older call sites.</summary>
public sealed partial class BridgeServer
{
    /// <summary>Alias used by older UI code.</summary>
    public void NoteRoadGraphDirtyAlias() => NoteRoadGraphDirty();

    public void QueueObjectUpsert(Guid id, ObjectPayload payload) => QueueObjectUpsert(payload);

    public void QueueObjectUpsert(string id, ObjectPayload payload) => QueueObjectUpsert(payload);
}
