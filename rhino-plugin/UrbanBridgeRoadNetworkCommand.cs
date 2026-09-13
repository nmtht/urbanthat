using Rhino;
using Rhino.Commands;
using Rhino.UI;

namespace UrbanBridge.Rhino;

/// <summary>Opens (or focuses) the Road Network dockable panel.</summary>
public sealed class UrbanBridgeRoadNetworkCommand : Command
{
    public override string EnglishName => "UrbanBridgeRoadNetwork";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var panelId = RoadNetworkPanel.PanelId;

        // Ensure panel type is registered (safe if already done in OnLoad).
        try
        {
            Panels.RegisterPanel(UrbanBridgePlugin.Instance, typeof(RoadNetworkPanel), "Road Network", null);
        }
        catch
        {
            // Already registered — ignore.
        }

        if (!Panels.IsPanelVisible(panelId))
            Panels.OpenPanel(panelId);
        else
            Panels.OpenPanel(panelId); // bring to front / select tab

        // Trigger a rebuild so the panel has data immediately.
        if (UrbanBridgePlugin.Instance?.Server is { } server && doc is not null)
            server.RebuildAndSendRoadNetwork(doc);

        RhinoApp.WriteLine("[UrbanBridge] Road Network panel opened.");
        return Result.Success;
    }
}
