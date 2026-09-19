using Eto.Drawing;
using Eto.Forms;
using Rhino;

namespace UrbanBridge.Plugin;

/// <summary>Architecture tab — custom UiButton Primary/Secondary.</summary>
public sealed class ArchitectureTabContent : Panel
{
    private readonly CheckBox _greenRoof = new()
    {
        Text = "Green roof on massing tops",
        Checked = false,
        TextColor = Colors.White,
    };
    private readonly CheckBox _withFacades = new()
    {
        Text = "Include facades with massing",
        Checked = true,
        TextColor = Colors.White,
    };
    private readonly CheckBox _withTrees = new()
    {
        Text = "Trees in green zones + roadside greenery",
        Checked = true,
        TextColor = Colors.White,
    };
    private readonly Label _status = new() { Text = "", TextColor = Colors.White };

    public ArchitectureTabContent()
    {
        BackgroundColor = UiTheme.PanelBg;

        _greenRoof.Checked = PluginSettings.GreenRoof;
        _withFacades.Checked = PluginSettings.GenerateFacadesWithMassing;
        _withTrees.Checked = PluginSettings.GenerateTreesForGreenZones;

        _greenRoof.CheckedChanged += (_, _) => PluginSettings.GreenRoof = _greenRoof.Checked == true;
        _withFacades.CheckedChanged += (_, _) => PluginSettings.GenerateFacadesWithMassing = _withFacades.Checked == true;
        _withTrees.CheckedChanged += (_, _) => PluginSettings.GenerateTreesForGreenZones = _withTrees.Checked == true;

        var facadesBtn = new UiButton("Generate facades (mesh)", UiButton.Style.Primary);
        facadesBtn.Click += (_, _) => RunFacades();
        facadesBtn.Width = 180;
        var treesBtn = new UiButton("Generate trees (mesh)", UiButton.Style.Secondary);
        treesBtn.Click += (_, _) => RunTrees();
        treesBtn.Width = 180;
        var bothBtn = new UiButton("Generate facades + trees", UiButton.Style.Primary);
        bothBtn.Click += (_, _) => { RunFacades(); RunTrees(); };
        bothBtn.Width = 180;

        var optsLayout = new DynamicLayout { Padding = 8, Spacing = new Size(4, 4), BackgroundColor = UiTheme.CardBg };
        optsLayout.AddRow(_withFacades);
        optsLayout.AddRow(_greenRoof);
        optsLayout.AddRow(_withTrees);

        var optsGroup = new GroupBox
        {
            Text = "Options",
            TextColor = Colors.White,
            BackgroundColor = UiTheme.CardBg,
            Content = optsLayout,
        };

        var genLayout = new DynamicLayout { Padding = 8, Spacing = new Size(4, 4), BackgroundColor = UiTheme.CardBg };
        genLayout.AddRow(facadesBtn);
        genLayout.AddRow(treesBtn);
        genLayout.AddRow(bothBtn);
        genLayout.AddRow(_status);

        var genGroup = new GroupBox
        {
            Text = "Generate",
            TextColor = Colors.White,
            BackgroundColor = UiTheme.CardBg,
            Content = genLayout,
        };

        var root = new DynamicLayout
        {
            Padding = 12,
            Spacing = new Size(8, 6),
            BackgroundColor = UiTheme.PanelBg,
        };
        root.AddRow(new Label
        {
            Text = "Architecture",
            Font = new Font(SystemFont.Bold, 13),
            TextColor = Colors.White,
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
            BackgroundColor = UiTheme.PanelBg,
            Content = root,
        };

        UiTheme.ApplyDark(this);
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
            _status.TextColor = Colors.White;
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
                _status.TextColor = Colors.White;
                return;
            }
            var n = new TreeGenerator(doc).GenerateForGreenZones(doc, analysis);
            _status.Text = $"Tree meshes: {n}";
            _status.TextColor = Colors.White;
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
