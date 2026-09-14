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

            _server = new BridgeServer();
            _server.Start();
            _documentEvents = new DocumentEventHandlers(_server);
            _documentEvents.Subscribe();
            RhinoApp.WriteLine("[UrbanBridge] Rhino bridge started at ws://localhost:7890.");

            if (Id != System.Guid.Empty)
            {
                try
                {
                    Panels.RegisterPanel(this, typeof(RoadNetworkPanel), "Road Network", null, PanelType.PerDoc);
                }
                catch (Exception ex)
                {
                    RhinoApp.WriteLine($"[UrbanBridge] RegisterPanel Road Network: {ex.Message}");
                }
            }

            RhinoApp.WriteLine("[UrbanBridge] Commands: UrbanBridgeRoadNetwork | UrbanBridgeZones | UrbanBridgeDashboard | UrbanBridgeStatus");
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
