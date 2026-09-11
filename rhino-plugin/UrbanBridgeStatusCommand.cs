using Rhino;
using Rhino.Commands;

namespace UrbanBridge.Rhino;

/// <summary>Shows the bridge state in Rhino's command history without opening a separate panel.</summary>
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
        return Result.Success;
    }
}
