using System.Runtime.InteropServices;
using Rhino;
using Rhino.Commands;
using Rhino.UI;

namespace UrbanBridge.Rhino;

/// <summary>Opens the unified UrbanBridge panel (Roads tab).</summary>
[Guid("F1A2B3C4-D5E6-4F7A-8B9C-0D1E2F3A4B5C")]
public sealed class UrbanBridgeRoadNetworkCommand : Command
{
    public override string EnglishName => "UrbanBridgeRoadNetwork";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        return OpenUnified(doc, tabIndex: 0);
    }

    internal static Result OpenUnified(RhinoDoc doc, int tabIndex)
    {
        var plugin = UrbanBridgePlugin.Instance;
        if (plugin is null)
        {
            RhinoApp.WriteLine("[UrbanBridge] Plugin instance is null.");
            return Result.Failure;
        }

        if (plugin.Id != System.Guid.Empty)
        {
            var panelType = typeof(RoadNetworkPanel);
            var panelId = panelType.GUID;
            try
            {
                Panels.RegisterPanel(plugin, panelType, "UrbanBridge", null, PanelType.PerDoc);
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"[UrbanBridge] RegisterPanel: {ex.Message}");
            }

            try
            {
                var ok = Panels.OpenPanelAsSibling(panelId, PanelIds.Layers, true);
                if (ok || Panels.IsPanelVisible(panelId))
                {
                    plugin.Server?.RebuildAndSendRoadNetwork(doc);
                    plugin.Server?.RebuildZoneAnalysis(doc);
                    return Result.Success;
                }

                Panels.OpenPanel(panelId);
                if (Panels.IsPanelVisible(panelId))
                {
                    plugin.Server?.RebuildAndSendRoadNetwork(doc);
                    plugin.Server?.RebuildZoneAnalysis(doc);
                    return Result.Success;
                }
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"[UrbanBridge] Panel open failed: {ex.Message}");
            }
        }

        RhinoApp.WriteLine("[UrbanBridge] Opening unified floating window…");
        RoadNetworkForm.ShowOrFocus(tabIndex);
        return Result.Success;
    }
}
