using System.Runtime.InteropServices;
using Eto.Drawing;
using Eto.Forms;
using Rhino.UI;

namespace UrbanBridge.Plugin;

[Guid("a3f8c2e1-9b4d-4e7a-8f1c-2d5e6a9b0c3d")]
public class RoadNetworkPanel : Panel, IPanel
{
    private readonly UrbanBridgeMainContent _content = new();

    public static Guid PanelId => typeof(RoadNetworkPanel).GUID;

    public RoadNetworkPanel()
    {
        BackgroundColor = UiTheme.PanelBg;
        Content = _content;
        _content.AttachServer(UrbanBridgePlugin.Instance?.Server);
        UiTheme.ApplyDark(this);
    }

    public void PanelShown(uint documentSerialNumber, ShowPanelReason reason)
    {
        _content.AttachServer(UrbanBridgePlugin.Instance?.Server);
        _content.RefreshActiveTab();
        UiTheme.ApplyDark(this);
    }

    public void PanelHidden(uint documentSerialNumber, ShowPanelReason reason)
    {
        // keep server attached; no teardown on hide
    }

    public void PanelClosing(uint documentSerialNumber, bool onCloseDocument)
    {
        if (onCloseDocument)
            _content.DetachServer();
    }
}
