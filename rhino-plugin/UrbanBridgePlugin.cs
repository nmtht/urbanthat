using System.Runtime.InteropServices;
using Rhino;
using Rhino.PlugIns;
using Rhino.UI;

namespace UrbanBridge.Rhino;

/// <summary>Entry point for the Rhino-side UrbanBridge synchronizer.</summary>
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
            var typeGuid = typeof(UrbanBridgePlugin).GUID;
            var asmGuid = GetType().Assembly.GetCustomAttributes(typeof(GuidAttribute), false)
                .OfType<GuidAttribute>()
                .FirstOrDefault()?.Value;

            RhinoApp.WriteLine($"[UrbanBridge] PlugIn.Id = {Id}");
            RhinoApp.WriteLine($"[UrbanBridge] Type.GUID = {typeGuid}");
            RhinoApp.WriteLine($"[UrbanBridge] Assembly Guid = {asmGuid}");

            // Always start the bridge — UI is secondary.
            _server = new BridgeServer();
            _server.Start();
            _documentEvents = new DocumentEventHandlers(_server);
            _documentEvents.Subscribe();
            RhinoApp.WriteLine("[UrbanBridge] Rhino bridge started at ws://localhost:7890.");

            if (Id == System.Guid.Empty)
            {
                RhinoApp.WriteLine("[UrbanBridge] WARNING: PlugIn.Id is still Empty — dockable panel may fail.");
                RhinoApp.WriteLine("[UrbanBridge] Use UrbanBridgeRoadNetwork for the floating window fallback.");
            }
            else
            {
                try
                {
                    Panels.RegisterPanel(
                        this,
                        typeof(RoadNetworkPanel),
                        "Road Network",
                        icon: null,
                        panelType: PanelType.PerDoc);
                    RhinoApp.WriteLine($"[UrbanBridge] Road Network panel registered. PanelGuid={typeof(RoadNetworkPanel).GUID}");
                }
                catch (Exception ex)
                {
                    RhinoApp.WriteLine($"[UrbanBridge] RegisterPanel failed: {ex.Message}");
                }
            }

            RhinoApp.WriteLine("[UrbanBridge] Open UI: UrbanBridgeRoadNetwork");
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
