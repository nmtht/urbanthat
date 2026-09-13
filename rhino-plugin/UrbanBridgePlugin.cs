using Rhino;
using Rhino.PlugIns;
using Rhino.UI;

namespace UrbanBridge.Rhino;

/// <summary>Entry point for the Rhino-side, one-way UrbanBridge synchronizer.</summary>
public sealed class UrbanBridgePlugin : PlugIn
{
    private BridgeServer? _server;
    private DocumentEventHandlers? _documentEvents;

    public UrbanBridgePlugin()
    {
        Instance = this;
    }

    public static UrbanBridgePlugin Instance { get; private set; } = null!;

    internal BridgeServer? Server => _server;

    protected override LoadReturnCode OnLoad(ref string errorMessage)
    {
        try
        {
            _server = new BridgeServer();
            _server.Start();
            _documentEvents = new DocumentEventHandlers(_server);
            _documentEvents.Subscribe();

            // Explicit PerDoc + non-null caption. Icon null is allowed; Mac still shows caption as tooltip.
            Panels.RegisterPanel(
                this,
                typeof(RoadNetworkPanel),
                "Road Network",
                icon: null,
                panelType: PanelType.PerDoc);

            var typeGuid = typeof(RoadNetworkPanel).GUID;
            RhinoApp.WriteLine($"[UrbanBridge] Rhino bridge started at ws://localhost:7890.");
            RhinoApp.WriteLine($"[UrbanBridge] Road Network panel registered. Guid={typeGuid}");
            RhinoApp.WriteLine("[UrbanBridge] Open panel: UrbanBridgeRoadNetwork");
            return LoadReturnCode.Success;
        }
        catch (Exception exception)
        {
            errorMessage = $"UrbanBridge could not start: {exception.Message}";
            RhinoApp.WriteLine($"[UrbanBridge] {errorMessage}");
            return LoadReturnCode.ErrorShowDialog;
        }
    }

    protected override void OnShutdown()
    {
        _documentEvents?.Dispose();
        _server?.Dispose();
        RhinoApp.WriteLine("[UrbanBridge] Rhino bridge stopped.");
        base.OnShutdown();
    }
}
