using System.Runtime.InteropServices;
using Rhino;
using Rhino.Commands;

namespace UrbanBridge.Rhino;

[Guid("A9B8C7D6-E5F4-4A3B-9C2D-1E0F9A8B7C6D")]
public sealed class UrbanBridgeStatusCommand : Command
{
    public override string EnglishName => "UrbanBridgeStatus";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var plugin = UrbanBridgePlugin.Instance;
        var server = plugin?.Server;

        RhinoApp.WriteLine($"[UrbanBridge] PlugIn.Id = {plugin?.Id}");
        RhinoApp.WriteLine($"[UrbanBridge] Type.GUID = {typeof(UrbanBridgePlugin).GUID}");

        if (server is null || !server.IsRunning)
        {
            RhinoApp.WriteLine("[UrbanBridge] The bridge is not running.");
            RhinoApp.WriteLine("[UrbanBridge] Try: disable/enable the plug-in in Options → Plug-ins, or reinstall and restart Rhino.");
            return Result.Failure;
        }

        RhinoApp.WriteLine($"[UrbanBridge] Running at ws://localhost:7890; clients: {server.ClientCount}.");
        RhinoApp.WriteLine("[UrbanBridge] UI command: UrbanBridgeRoadNetwork");

        var graph = server.LatestRoadNetwork;
        if (graph is not null)
        {
            RhinoApp.WriteLine(
                $"[UrbanBridge] Network: {graph.Edges.Count} edges, {graph.Nodes.Count} nodes, " +
                $"{graph.Issues.Count} issues, {graph.Stats.TotalLengthM:F1} m.");
        }
        else
        {
            RhinoApp.WriteLine("[UrbanBridge] Network not built yet (layer Roads + curves)." );
        }

        return Result.Success;
    }
}
