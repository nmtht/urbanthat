using Eto.Forms;
using Rhino;

namespace UrbanBridge.Rhino;

/// <summary>Aggregates zone + road network stats (Stage 2.2 §7).</summary>
public sealed class ProjectDashboardForm : Form
{
    private static ProjectDashboardForm? _instance;

    private readonly Label _popLabel = new() { Text = "Population: —" };
    private readonly Label _jobsLabel = new() { Text = "Jobs: —" };
    private readonly Label _greenLabel = new() { Text = "Green: —" };
    private readonly Label _roadDensityLabel = new() { Text = "Road density: —" };
    private readonly Label _staleLabel = new() { Text = "", TextColor = Eto.Drawing.Colors.DarkOrange };
    private readonly TextArea _areaByType = new() { ReadOnly = true, Wrap = true, Height = 140 };
    private readonly TextArea _roadStats = new() { ReadOnly = true, Wrap = true, Height = 100 };

    private BridgeServer? _server;

    private ProjectDashboardForm()
    {
        Title = "UrbanBridge — Project Dashboard";
        ClientSize = new Eto.Drawing.Size(440, 480);
        MinimumSize = new Eto.Drawing.Size(320, 360);
        Padding = 10;

        var refresh = new Button { Text = "Refresh" };
        refresh.Click += (_, _) => Rebuild();

        Content = new StackLayout
        {
            Spacing = 6,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                new Label { Text = "Project Dashboard" },
                _staleLabel,
                _popLabel,
                _jobsLabel,
                _greenLabel,
                _roadDensityLabel,
                new Label { Text = "Area by zone_type" },
                _areaByType,
                new Label { Text = "Road network" },
                _roadStats,
                refresh,
            },
        };

        Closed += (_, _) =>
        {
            if (_server is not null)
            {
                _server.ZoneAnalysisUpdated -= OnZone;
                _server.RoadNetworkUpdated -= OnRoad;
            }
            if (ReferenceEquals(_instance, this))
                _instance = null;
        };
    }

    public static void ShowOrFocus()
    {
        if (_instance is not null)
        {
            _instance.BringToFront();
            _instance.Rebuild();
            return;
        }

        var form = new ProjectDashboardForm();
        _instance = form;
        form.Owner = global::Rhino.UI.RhinoEtoApp.MainWindow;
        form._server = UrbanBridgePlugin.Instance?.Server;
        if (form._server is not null)
        {
            form._server.ZoneAnalysisUpdated += form.OnZone;
            form._server.RoadNetworkUpdated += form.OnRoad;
        }
        form.Rebuild();
        form.Show();
    }

    private void Rebuild()
    {
        try
        {
            if (RhinoDoc.ActiveDoc is { } doc && UrbanBridgePlugin.Instance?.Server is { } server)
            {
                server.RebuildAndSendRoadNetwork(doc);
                server.RebuildZoneAnalysis(doc);
            }
            Apply(UrbanBridgePlugin.Instance?.Server?.LatestZoneAnalysis,
                  UrbanBridgePlugin.Instance?.Server?.LatestRoadNetwork);
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"[UrbanBridge] Dashboard refresh error: {ex.Message}");
        }
    }

    private void OnZone(ZoneAnalysis a) => Apply(a, _server?.LatestRoadNetwork);
    private void OnRoad(RoadNetworkGraph g) => Apply(_server?.LatestZoneAnalysis, g);

    private void Apply(ZoneAnalysis? zones, RoadNetworkGraph? roads)
    {
        void Ui()
        {
            if (zones is null)
            {
                _popLabel.Text = "Population: —";
                _jobsLabel.Text = "Jobs: —";
                _greenLabel.Text = "Green: —";
                _areaByType.Text = "No zone data.";
            }
            else
            {
                _popLabel.Text = $"Population (est.): {zones.TotalPopulation:F0}";
                _jobsLabel.Text = $"Jobs (est.): {zones.TotalJobs:F0}";
                var greenPct = zones.TotalAreaSqm > 0
                    ? 100.0 * zones.TotalGreenAreaSqm / zones.TotalAreaSqm
                    : 0;
                _greenLabel.Text =
                    $"Green area: {zones.TotalGreenAreaSqm:F0} m² ({greenPct:F1}% of zones)";

                if (zones.AreaByType.Count == 0)
                    _areaByType.Text = "—";
                else
                {
                    _areaByType.Text = string.Join("\n",
                        zones.AreaByType.OrderBy(kv => kv.Key)
                            .Select(kv => $"{kv.Key,-12} {kv.Value,10:F0} m²"));
                }

                _staleLabel.Text = zones.RoadSurfacesStale
                    ? "⚠ Road surfaces outdated relative to centerline edits."
                    : "";
            }

            if (roads is null)
            {
                _roadStats.Text = "No road graph.";
                _roadDensityLabel.Text = "Road density: —";
            }
            else
            {
                _roadStats.Text =
                    $"Length: {roads.Stats.TotalLengthM:F1} m\n" +
                    $"Intersections: {roads.Stats.IntersectionCount} · Dead ends: {roads.Stats.DeadEndCount}\n" +
                    $"Components: {roads.Stats.ComponentCount}";

                // km of road per km² of zones
                var zoneKm2 = (zones?.TotalAreaSqm ?? 0) / 1_000_000.0;
                var roadKm = roads.Stats.TotalLengthM / 1000.0;
                if (zoneKm2 > 1e-9)
                    _roadDensityLabel.Text = $"Road density: {roadKm / zoneKm2:F2} km/km²";
                else
                    _roadDensityLabel.Text = "Road density: — (no zone area)";
            }
        }

        try
        {
            if (Application.Instance != null)
                Application.Instance.AsyncInvoke(Ui);
            else
                Ui();
        }
        catch { Ui(); }
    }
}

[System.Runtime.InteropServices.Guid("C3D4E5F6-A7B8-4C9D-0E1F-2A3B4C5D6E7F")]
public sealed class UrbanBridgeDashboardCommand : global::Rhino.Commands.Command
{
    public override string EnglishName => "UrbanBridgeDashboard";

    protected override global::Rhino.Commands.Result RunCommand(RhinoDoc doc, global::Rhino.Commands.RunMode mode)
    {
        ProjectDashboardForm.ShowOrFocus();
        return global::Rhino.Commands.Result.Success;
    }
}
