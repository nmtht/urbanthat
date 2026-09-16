using Rhino;
using Rhino.Commands;
using Rhino.UI;

namespace UrbanBridge.Plugin;

[System.Runtime.InteropServices.Guid("b2c3d4e5-f6a7-8901-bcde-f12345678901")]
public class UrbanBridgeRoadNetworkCommand : Command
{
    public override string EnglishName => "UrbanBridgeRoadNetwork";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        try
        {
            Panels.OpenPanel(typeof(RoadNetworkPanel));
            RhinoApp.WriteLine("[UrbanBridge] Road Network panel opened.");
            UrbanBridgePlugin.Instance?.Server?.RebuildAndSendRoadNetwork(doc);
            return Result.Success;
        }
        catch (System.Exception ex)
        {
            RhinoApp.WriteLine($"[UrbanBridge] Open panel failed: {ex.Message}");
            return Result.Failure;
        }
    }
}
