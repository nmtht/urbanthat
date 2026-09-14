using Eto.Forms;
using Rhino;
using Rhino.UI;

namespace UrbanBridge.Rhino;

/// <summary>Rhino dockable panel for road-network stats, issues, and attribute editing.</summary>
[System.Runtime.InteropServices.Guid("A3F8C2E1-9B4D-4E7A-8F1C-2D5E6A9B0C3D")]
public class RoadNetworkPanel : Panel, IPanel
{
    private readonly RoadNetworkContent _content = new();

    public static readonly System.Guid PanelId = new("A3F8C2E1-9B4D-4E7A-8F1C-2D5E6A9B0C3D");

    public RoadNetworkPanel()
    {
        Content = _content;
        RhinoApp.WriteLine("[UrbanBridge] RoadNetworkPanel instance created.");
    }

    public void PanelShown(uint documentSerialNumber, ShowPanelReason reason)
    {
        RhinoApp.WriteLine($"[UrbanBridge] PanelShown reason={reason} docSN={documentSerialNumber}");
        if (reason is not (ShowPanelReason.Show or ShowPanelReason.ShowOnDeactivate))
            return;

        _content.AttachServer(UrbanBridgePlugin.Instance?.Server);
        _content.RebuildGraph();
        _content.RefreshSelectionLabel();
    }

    public void PanelHidden(uint documentSerialNumber, ShowPanelReason reason)
    {
        RhinoApp.WriteLine($"[UrbanBridge] PanelHidden reason={reason}");
        if (reason == ShowPanelReason.HideOnDeactivate)
            return;
        _content.DetachServer();
    }

    public void PanelClosing(uint documentSerialNumber, bool onCloseDocument)
    {
        RhinoApp.WriteLine($"[UrbanBridge] PanelClosing onCloseDocument={onCloseDocument}");
        _content.DetachServer();
    }
}
