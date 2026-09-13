using Rhino;
using Rhino.Commands;
using Rhino.UI;

namespace UrbanBridge.Rhino;

/// <summary>Shows the bridge state and how to open the Road Network panel.</summary>
public sealed class UrbanBridgeStatusCommand : Command
{
    public override string EnglishName => "UrbanBridgeStatus";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var server = UrbanBridgePlugin.Instance?.Server;
        if (server is null || !server.IsRunning)
        {
            RhinoApp.WriteLine("[UrbanBridge] The bridge is not running.");
            return Result.Failure;
        }

        RhinoApp.WriteLine($"[UrbanBridge] Running at ws://localhost:7890; connected clients: {server.ClientCount}.");
        RhinoApp.WriteLine("[UrbanBridge] Open road panel: type UrbanBridgeRoadNetwork");

        var graph = server.LatestRoadNetwork;
        if (graph is not null)
        {
            RhinoApp.WriteLine(
                $"[UrbanBridge] Road network: {graph.Edges.Count} edges, {graph.Nodes.Count} nodes, " +
                $"{graph.Issues.Count} issues, length {graph.Stats.TotalLengthM:F1} m.");
        }
        else
        {
            RhinoApp.WriteLine("[UrbanBridge] Road network not built yet (add curves on layer Roads).");
        }

        return Result.Success;
    }
}
