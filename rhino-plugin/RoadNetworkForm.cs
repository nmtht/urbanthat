using Eto.Forms;
using Rhino;

namespace UrbanBridge.Rhino;

/// <summary>
/// Modeless Eto window — works even when PlugIn.Id is Empty and dockable panels cannot register.
/// </summary>
public sealed class RoadNetworkForm : Form
{
    private static RoadNetworkForm? _openInstance;
    private readonly RoadNetworkContent _content = new();

    private RoadNetworkForm()
    {
        Title = "UrbanBridge — Road Network";
        ClientSize = new Eto.Drawing.Size(440, 560);
        MinimumSize = new Eto.Drawing.Size(320, 400);
        Padding = 0;
        Content = _content;

        Closed += (_, _) =>
        {
            _content.DetachServer();
            if (ReferenceEquals(_openInstance, this))
                _openInstance = null;
        };
    }

    public static void ShowOrFocus()
    {
        if (_openInstance is not null)
        {
            _openInstance.BringToFront();
            _openInstance._content.RebuildGraph();
            _openInstance._content.RefreshSelectionLabel();
            return;
        }

        var form = new RoadNetworkForm();
        _openInstance = form;
        form.Owner = global::Rhino.UI.RhinoEtoApp.MainWindow;
        form._content.AttachServer(UrbanBridgePlugin.Instance?.Server);
        form._content.RebuildGraph();
        form._content.RefreshSelectionLabel();
        form.Show();
    }
}
