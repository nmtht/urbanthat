using Eto.Forms;
using Rhino;

namespace UrbanBridge.Rhino;

/// <summary>
/// Single modeless window for the whole plugin (Roads / Zoning / Dashboard tabs).
/// </summary>
public sealed class RoadNetworkForm : Form
{
    private static RoadNetworkForm? _openInstance;
    private readonly UrbanBridgeMainContent _content = new();

    private RoadNetworkForm()
    {
        Title = "UrbanBridge";
        ClientSize = new Eto.Drawing.Size(460, 620);
        MinimumSize = new Eto.Drawing.Size(340, 420);
        Padding = 0;
        Content = _content;

        Closed += (_, _) =>
        {
            _content.DetachServer();
            if (ReferenceEquals(_openInstance, this))
                _openInstance = null;
        };
    }

    /// <param name="tabIndex">0=Roads, 1=Zoning, 2=Dashboard</param>
    public static void ShowOrFocus(int tabIndex = 0)
    {
        if (_openInstance is not null)
        {
            _openInstance.BringToFront();
            _openInstance._content.SelectTab(tabIndex);
            _openInstance._content.RefreshAll();
            return;
        }

        var form = new RoadNetworkForm();
        _openInstance = form;
        form.Owner = global::Rhino.UI.RhinoEtoApp.MainWindow;
        form._content.AttachServer(UrbanBridgePlugin.Instance?.Server);
        form._content.SelectTab(tabIndex);
        form._content.RefreshAll();
        form.Show();
    }
}
