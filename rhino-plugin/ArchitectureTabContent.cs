using Eto.Drawing;
using Eto.Forms;
using Rhino;

namespace UrbanBridge.Plugin;

/// <summary>Architecture: facades, green roof, trees — same DynamicLayout theme as Zoning.</summary>
public sealed class ArchitectureTabContent : Panel
{
    private readonly CheckBox _greenRoof = new()
    {
        Text = "Green roof on massing tops",
        Checked = false,
        TextColor = Colors.Black,
    };
    private readonly CheckBox _withFacades = new()
    {
        Text = "Include facades with massing",
        Checked = true,
        TextColor = Colors.Black,
    };
    private readonly CheckBox _withTrees = new()
    {
        Text = "Trees in green zones + roadside greenery",
        Checked = true,
        TextColor = Colors.Black,
    };
    private readonly Label _status = new() { Text = "", TextColor = Colors.Black };

    public ArchitectureTabContent()
    {
        BackgroundColor = Colors.White;

        _greenRoof.Checked = PluginSettings.GreenRoof;
        _withFacades.Checked = PluginSettings.GenerateFacadesWithMassing;
        _withTrees.Checked = PluginSettings.GenerateTreesForGreenZones;

        _greenRoof.CheckedChanged += (_, _) => PluginSettings.GreenRoof = _greenRoof.Checked == true;
        _withFacades.CheckedChanged += (_, _) => PluginSettings.GenerateFacadesWithMassing = _withFacades.Checked == true;
        _withTrees.CheckedChanged += (_, _) => PluginSettings.GenerateTreesForGreenZones = _withTrees.Checked == true;

        var facadesBtn = new Button { Text = "Generate facades (mesh)" };
        facadesBtn.Click += (_, _) => RunFacades();
        var treesBtn = new Button { Text = "Generate trees (mesh)" };
        treesBtn.Click += (_, _) => RunTrees();
        var bothBtn = new Button { Text = "Generate facades + trees" };
        bothBtn.Click += (_, _) => { RunFacades(); RunTrees(); };

        var optsLayout = new DynamicLayout { Padding = 8, Spacing = new Size(4, 4) };
        optsLayout.AddRow(_withFacades);
        optsLayout.AddRow(_greenRoof);
        optsLayout.AddRow(_withTrees);

        var optsGroup = new GroupBox
        {
            Text = "Options",
            Content = optsLayout,
        };

        var genLayout = new DynamicLayout { Padding = 8, Spacing = new Size(4, 4) };
        genLayout.AddRow(facadesBtn);
        genLayout.AddRow(treesBtn);
        genLayout.AddRow(bothBtn);
        genLayout.AddRow(_status);

        var genGroup = new GroupBox
        {
            Text = "Generate",
            Content = genLayout,
        };

        var root = new DynamicLayout
        {
            Padding = 12,
            Spacing = new Size(8, 6),
        };
        root.AddRow(new Label
        {
            Text = "Architecture",
            Font = new Font(SystemFont.Bold, 13),
            TextColor = Colors.Black,
        });
        root.AddRow(new Label
        {
            Text = "Mesh facades follow massing face orientation (incl. rotated pads).\n" +
                   "Green roof = mesh patch on top faces. Trees = trunk + oval crown.",
            TextColor = UiTheme.Muted,
        });
        root.AddRow(optsGroup);
        root.AddRow(genGroup);

        Content = new Scrollable
        {
            Border = BorderType.None,
            BackgroundColor = Colors.White,
            Content = root,
        };
    }

    public void AttachServer(BridgeServer? server) { }
    public void DetachServer() { }
    public void PaintFromCache() { }

    private void RunFacades()
    {
        try
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc is null) return;
            var n = new FacadeGenerator(doc).GenerateFromMassing(doc, PluginSettings.GreenRoof);
            _status.Text = $"Facades/roof meshes: {n}";
            _status.TextColor = Colors.Black;
            RhinoApp.WriteLine($"[UrbanBridge] Facades/roof: {n}");
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
            _status.TextColor = UiTheme.Danger;
            RhinoApp.WriteLine($"[UrbanBridge] Facades failed: {ex.Message}");
        }
    }

    private void RunTrees()
    {
        try
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc is null) return;
            var server = UrbanBridgePlugin.Instance?.Server;
            if (server is null) return;
            server.RebuildZoneAnalysis(doc);
            var analysis = server.LatestZoneAnalysis;
            if (analysis is null)
            {
                _status.Text = "No zones.";
                _status.TextColor = Colors.Black;
                return;
            }
            var n = new TreeGenerator(doc).GenerateForGreenZones(doc, analysis);
            _status.Text = $"Tree meshes: {n}";
            _status.TextColor = Colors.Black;
            RhinoApp.WriteLine($"[UrbanBridge] Trees: {n}");
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
            _status.TextColor = UiTheme.Danger;
            RhinoApp.WriteLine($"[UrbanBridge] Trees failed: {ex.Message}");
        }
    }
}
