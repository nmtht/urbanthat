using Rhino;
using Rhino.Commands;

namespace UrbanBridge.Plugin;

[System.Runtime.InteropServices.Guid("a1b2c3d4-e5f6-7890-abcd-ef1234567890")]
public class UrbanBridgeStatusCommand : Command
{
    public override string EnglishName => "UrbanBridgeStatus";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var plugin = UrbanBridgePlugin.Instance;
        RhinoApp.WriteLine($"[UrbanBridge] PlugIn.Id = {plugin?.Id}");
        var server = plugin?.Server;
        if (server is null || !server.IsRunning)
        {
            RhinoApp.WriteLine("[UrbanBridge] The bridge is not running.");
            return Result.Success;
        }

        RhinoApp.WriteLine(
            $"[UrbanBridge] Running at ws://localhost:7890; connected clients: {server.ConnectedClientCount}.");
        RhinoApp.WriteLine("[UrbanBridge] Open road panel: type UrbanBridgeRoadNetwork");

        var g = server.LatestRoadNetwork;
        if (g is null)
            RhinoApp.WriteLine("[UrbanBridge] Road network: not built yet.");
        else
            RhinoApp.WriteLine(
                $"[UrbanBridge] Road network: {g.Edges.Count} edges, {g.Nodes.Count} nodes, " +
                $"{g.Issues.Count} issues, length {g.Stats.TotalLengthM:F1} m.");

        return Result.Success;
    }
}
