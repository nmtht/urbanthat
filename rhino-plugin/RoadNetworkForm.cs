using Eto.Forms;
using Rhino;

namespace UrbanBridge.Plugin;

/// <summary>Legacy form host — delegates to UrbanBridgeMainContent.</summary>
public sealed class RoadNetworkForm : Form
{
    private readonly UrbanBridgeMainContent _content = new();

    public RoadNetworkForm()
    {
        Title = "UrbanBridge";
        ClientSize = new Eto.Drawing.Size(420, 640);
        Content = _content;
        _content.AttachServer(UrbanBridgePlugin.Instance?.Server);
    }

    protected override void OnClosed(EventArgs e)
    {
        _content.DetachServer();
        base.OnClosed(e);
    }
}
