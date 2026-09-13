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
        var panelType = typeof(RoadNetworkPanel);
        var panelId = panelType.GUID;

        RhinoApp.WriteLine($"[UrbanBridge] Panel type GUID: {panelId}");
        RhinoApp.WriteLine($"[UrbanBridge] Static PanelId:   {RoadNetworkPanel.PanelId}");
        RhinoApp.WriteLine($"[UrbanBridge] IsPanelVisible:   {Panels.IsPanelVisible(panelId)}");

        // Re-register is safe; ensures type is known after hot-reload / partial load.
        try
        {
            Panels.RegisterPanel(
                UrbanBridgePlugin.Instance,
                panelType,
                "Road Network",
                icon: null,
                panelType: PanelType.PerDoc);
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"[UrbanBridge] RegisterPanel note: {ex.Message}");
        }

        // Prefer sibling of Layers — more reliable on Mac than floating OpenPanel alone.
        var openedAsSibling = false;
        try
        {
            openedAsSibling = Panels.OpenPanelAsSibling(panelId, PanelIds.Layers, makeSelectedPanel: true);
            RhinoApp.WriteLine($"[UrbanBridge] OpenPanelAsSibling(Layers): {openedAsSibling}");
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"[UrbanBridge] OpenPanelAsSibling failed: {ex.Message}");
        }

        if (!openedAsSibling)
        {
            try
            {
                Panels.OpenPanel(panelId);
                RhinoApp.WriteLine("[UrbanBridge] OpenPanel(Guid) called.");
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"[UrbanBridge] OpenPanel(Guid) failed: {ex.Message}");
            }

            try
            {
                Panels.OpenPanel(panelType);
                RhinoApp.WriteLine("[UrbanBridge] OpenPanel(Type) called.");
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"[UrbanBridge] OpenPanel(Type) failed: {ex.Message}");
            }
        }

        RhinoApp.WriteLine($"[UrbanBridge] IsPanelVisible after open: {Panels.IsPanelVisible(panelId)}");

        if (UrbanBridgePlugin.Instance?.Server is { } server && doc is not null)
            server.RebuildAndSendRoadNetwork(doc);

        // Hint for Mac users where the tab may appear.
        RhinoApp.WriteLine("[UrbanBridge] Look for tab 'Road Network' next to Layers / Properties, or Window → Panels.");
        return Result.Success;
    }
}
