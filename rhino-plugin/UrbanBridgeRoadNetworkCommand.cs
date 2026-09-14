using System.Runtime.InteropServices;
using Rhino;
using Rhino.Commands;
using Rhino.UI;

namespace UrbanBridge.Rhino;

/// <summary>Opens (or focuses) the Road Network dockable panel.</summary>
[Guid("F1A2B3C4-D5E6-4F7A-8B9C-0D1E2F3A4B5C")]
public sealed class UrbanBridgeRoadNetworkCommand : Command
{
    public override string EnglishName => "UrbanBridgeRoadNetwork";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var plugin = UrbanBridgePlugin.Instance;
        if (plugin is null)
        {
            RhinoApp.WriteLine("[UrbanBridge] Plugin instance is null.");
            return Result.Failure;
        }

        RhinoApp.WriteLine($"[UrbanBridge] PlugIn.Id = {plugin.Id}");
        if (plugin.Id == System.Guid.Empty)
        {
            RhinoApp.WriteLine("[UrbanBridge] ERROR: PlugIn.Id is empty. Rebuild with GuidAttribute on UrbanBridgePlugin.");
            return Result.Failure;
        }

        var panelType = typeof(RoadNetworkPanel);
        var panelId = panelType.GUID;

        RhinoApp.WriteLine($"[UrbanBridge] Panel type GUID: {panelId}");
        RhinoApp.WriteLine($"[UrbanBridge] IsPanelVisible before: {Panels.IsPanelVisible(panelId)}");

        try
        {
            Panels.RegisterPanel(
                plugin,
                panelType,
                "Road Network",
                icon: null,
                panelType: PanelType.PerDoc);
            RhinoApp.WriteLine("[UrbanBridge] RegisterPanel OK");
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"[UrbanBridge] RegisterPanel: {ex.Message}");
        }

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
                RhinoApp.WriteLine("[UrbanBridge] OpenPanel(Guid) called");
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"[UrbanBridge] OpenPanel(Guid) failed: {ex.Message}");
            }

            try
            {
                Panels.OpenPanel(panelType);
                RhinoApp.WriteLine("[UrbanBridge] OpenPanel(Type) called");
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"[UrbanBridge] OpenPanel(Type) failed: {ex.Message}");
            }
        }

        RhinoApp.WriteLine($"[UrbanBridge] IsPanelVisible after: {Panels.IsPanelVisible(panelId)}");

        if (plugin.Server is { } server && doc is not null)
            server.RebuildAndSendRoadNetwork(doc);

        RhinoApp.WriteLine("[UrbanBridge] Look for tab 'Road Network' next to Layers / Properties.");
        return Result.Success;
    }
}
