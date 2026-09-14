using Eto.Forms;
using Rhino;
using Rhino.UI;

namespace UrbanBridge.Rhino;

/// <summary>Dockable panel — unified UrbanBridge UI (tabs).</summary>
[System.Runtime.InteropServices.Guid("A3F8C2E1-9B4D-4E7A-8F1C-2D5E6A9B0C3D")]
public class RoadNetworkPanel : Panel, IPanel
{
    private readonly UrbanBridgeMainContent _content = new();

    public static readonly System.Guid PanelId = new("A3F8C2E1-9B4D-4E7A-8F1C-2D5E6A9B0C3D");

    public RoadNetworkPanel()
    {
        Content = _content;
        RhinoApp.WriteLine("[UrbanBridge] Unified panel instance created.");
    }

    public void PanelShown(uint documentSerialNumber, ShowPanelReason reason)
    {
        if (reason is not (ShowPanelReason.Show or ShowPanelReason.ShowOnDeactivate))
            return;

        _content.AttachServer(UrbanBridgePlugin.Instance?.Server);
        _content.RefreshActiveTab(fromCacheOnly: true);
    }

    public void PanelHidden(uint documentSerialNumber, ShowPanelReason reason)
    {
        if (reason == ShowPanelReason.HideOnDeactivate)
            return;
        _content.DetachServer();
    }

    public void PanelClosing(uint documentSerialNumber, bool onCloseDocument)
    {
        _content.DetachServer();
    }
}
