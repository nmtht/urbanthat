using System.Runtime.InteropServices;
using Rhino;
using Rhino.PlugIns;
using Rhino.UI;

namespace UrbanBridge.Plugin;

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
            RhinoApp.WriteLine($"[UrbanBridge] PlugIn.Id = {Id}");

            _server = new BridgeServer();
            _server.Start();

            _documentEvents = new DocumentEventHandlers(_server);
            _documentEvents.Register();

            Panels.RegisterPanel(this, typeof(RoadNetworkPanel), "UrbanBridge", null);
            RhinoApp.WriteLine("[UrbanBridge] Panel registered. Command: UrbanBridgeRoadNetwork");
            return LoadReturnCode.Success;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            RhinoApp.WriteLine($"[UrbanBridge] OnLoad failed: {ex}");
            return LoadReturnCode.ErrorShowDialog;
        }
    }

    protected override void OnShutdown()
    {
        _documentEvents?.Unregister();
        _documentEvents = null;
        _server?.Dispose();
        _server = null;
        base.OnShutdown();
    }
}
