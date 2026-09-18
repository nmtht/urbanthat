using Eto.Forms;
using Rhino;

namespace UrbanBridge.Plugin;

/// <summary>Architecture: facades, green roof, trees.</summary>
public sealed class ArchitectureTabContent : Panel
{
    private readonly CheckBox _greenRoof = new() { Text = "Green roof on massing tops", Checked = false };
    private readonly CheckBox _withFacades = new() { Text = "Include facades", Checked = true };
    private readonly CheckBox _withTrees = new() { Text = "Trees in green zones + roadside greenery", Checked = true };
    private readonly Label _status = new() { Text = "" };

    public ArchitectureTabContent()
    {
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

        Content = new Scrollable
        {
            Border = BorderType.None,
            Content = new StackLayout
            {
                Padding = 10, Spacing = 8,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    new Label { Text = "Architecture", Font = new Eto.Drawing.Font(Eto.Drawing.SystemFont.Bold, 12) },
                    new Label
                    {
                        Text = "Mesh facades follow massing face orientation (incl. rotated pads).\n" +
                               "Green roof = mesh patch on top faces. Trees = trunk + oval crown.",
                        TextColor = Eto.Drawing.Colors.Gray,
                    },
                    _withFacades,
                    _greenRoof,
                    _withTrees,
                    facadesBtn,
                    treesBtn,
                    bothBtn,
                    _status,
                },
            },
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
            RhinoApp.WriteLine($"[UrbanBridge] Facades/roof: {n}");
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
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
                return;
            }
            var n = new TreeGenerator(doc).GenerateForGreenZones(doc, analysis);
            _status.Text = $"Tree meshes: {n}";
            RhinoApp.WriteLine($"[UrbanBridge] Trees: {n}");
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
            RhinoApp.WriteLine($"[UrbanBridge] Trees failed: {ex.Message}");
        }
    }
}
