using System.Runtime.InteropServices;
using Rhino;
using Rhino.Commands;
using Rhino.UI;

namespace UrbanBridge.Rhino;

/// <summary>Opens the Road Network UI (dockable panel if possible, otherwise floating window).</summary>
[Guid("F1A2B3C4-D5E6-4F7A-8B9C-0D1E2F3A4B5C")]
public sealed class UrbanBridgeRoadNetworkCommand : Command
{
    public override string EnglishName => "UrbanBridgeRoadNetwork";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var plugin = UrbanBridgePlugin.Instance;
        if (plugin is null)
        {
            RhinoApp.WriteLine("[UrbanBridge] Plugin instance is null — is the plug-in loaded?");
            return Result.Failure;
        }

        RhinoApp.WriteLine($"[UrbanBridge] PlugIn.Id = {plugin.Id}");
        RhinoApp.WriteLine($"[UrbanBridge] Type.GUID = {typeof(UrbanBridgePlugin).GUID}");
        RhinoApp.WriteLine($"[UrbanBridge] Server running = {plugin.Server?.IsRunning == true}");

        // Prefer dockable panel when Id is valid.
        if (plugin.Id != System.Guid.Empty)
        {
            var panelType = typeof(RoadNetworkPanel);
            var panelId = panelType.GUID;
            try
            {
                Panels.RegisterPanel(plugin, panelType, "Road Network", null, PanelType.PerDoc);
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"[UrbanBridge] RegisterPanel: {ex.Message}");
            }

            try
            {
                var ok = Panels.OpenPanelAsSibling(panelId, PanelIds.Layers, true);
                RhinoApp.WriteLine($"[UrbanBridge] OpenPanelAsSibling: {ok}, visible={Panels.IsPanelVisible(panelId)}");
                if (ok || Panels.IsPanelVisible(panelId))
                {
                    plugin.Server?.RebuildAndSendRoadNetwork(doc);
                    return Result.Success;
                }

                Panels.OpenPanel(panelId);
                if (Panels.IsPanelVisible(panelId))
                {
                    plugin.Server?.RebuildAndSendRoadNetwork(doc);
                    return Result.Success;
                }
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"[UrbanBridge] Panel open failed: {ex.Message}");
            }
        }

        // Fallback: modeless Eto window (always works on Mac/Windows).
        RhinoApp.WriteLine("[UrbanBridge] Opening floating Road Network window…");
        RoadNetworkForm.ShowOrFocus();
        return Result.Success;
    }
}
