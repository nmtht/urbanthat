using System.Runtime.InteropServices;
using Rhino;
using Rhino.PlugIns;
using Rhino.UI;

namespace UrbanBridge.Rhino;

/// <summary>Entry point for the Rhino-side, one-way UrbanBridge synchronizer.</summary>
/// <remarks>
/// GuidAttribute is required: without it PlugIn.Id is Guid.Empty and
/// Panels.RegisterPanel throws (panel never appears in the UI).
/// </remarks>
[Guid("E8C3A1F2-4B7D-4E9A-8C1F-3D6E9B0A2C5D")]
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
            RhinoApp.WriteLine($"[UrbanBridge] PlugIn.Id = {Id}");
            if (Id == System.Guid.Empty)
            {
                errorMessage = "UrbanBridge PlugIn.Id is Guid.Empty — GuidAttribute missing on plugin class.";
                RhinoApp.WriteLine($"[UrbanBridge] {errorMessage}");
                return LoadReturnCode.ErrorShowDialog;
            }

            _server = new BridgeServer();
            _server.Start();
            _documentEvents = new DocumentEventHandlers(_server);
            _documentEvents.Subscribe();

            Panels.RegisterPanel(
                this,
                typeof(RoadNetworkPanel),
                "Road Network",
                icon: null,
                panelType: PanelType.PerDoc);

            RhinoApp.WriteLine("[UrbanBridge] Rhino bridge started at ws://localhost:7890.");
            RhinoApp.WriteLine($"[UrbanBridge] Road Network panel registered. PanelGuid={typeof(RoadNetworkPanel).GUID}");
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
