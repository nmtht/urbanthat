using Eto.Forms;
using Rhino;

namespace UrbanBridge.Plugin;

/// <summary>Dashboard tab — paints from cached Latest* only.</summary>
public sealed class DashboardTabContent : Panel
{
    private readonly Label _popLabel = new() { Text = "Population: —" };
    private readonly Label _jobsLabel = new() { Text = "Jobs: —" };
    private readonly Label _greenLabel = new() { Text = "Green: —" };
    private readonly Label _roadDensityLabel = new() { Text = "Road density: —" };
    private readonly Label _builtGfaLabel = new() { Text = "Built floor area: —" };
    private readonly Label _staleLabel = new() { Text = "", TextColor = Eto.Drawing.Colors.DarkOrange };
    private readonly TextArea _areaByType = new() { ReadOnly = true, Wrap = true, Height = 120 };
    private readonly TextArea _roadStats = new() { ReadOnly = true, Wrap = true, Height = 80 };

    private BridgeServer? _server;
    private readonly object _uiGate = new();
    private System.Threading.Timer? _uiTimer;

    public DashboardTabContent()
    {
        var refresh = new Button { Text = "Refresh (recompute)" };
        refresh.Click += (_, _) => ForceRecompute();

        Content = new Scrollable
        {
            Border = BorderType.None,
            Content = new StackLayout
            {
                Padding = 10,
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
                    _builtGfaLabel,
                    new Label { Text = "Area by zone_type" },
                    _areaByType,
                    new Label { Text = "Road network" },
                    _roadStats,
                    refresh,
                },
            },
        };
    }

    public void AttachServer(BridgeServer? server)
    {
        if (_server is not null)
        {
            _server.ZoneAnalysisUpdated -= OnZone;
            _server.RoadNetworkUpdated -= OnRoad;
        }
        _server = server;
        if (_server is not null)
        {
            _server.ZoneAnalysisUpdated += OnZone;
            _server.RoadNetworkUpdated += OnRoad;
            PaintFromCache();
        }
    }

    public void DetachServer()
    {
        if (_server is not null)
        {
            _server.ZoneAnalysisUpdated -= OnZone;
            _server.RoadNetworkUpdated -= OnRoad;
        }
        _server = null;
        UiInvoke.DisposeTimer(ref _uiTimer, _uiGate);
    }

    public void PaintFromCache() =>
        Apply(_server?.LatestZoneAnalysis, _server?.LatestRoadNetwork);

    private void ForceRecompute()
    {
        try
        {
            if (RhinoDoc.ActiveDoc is { } doc && UrbanBridgePlugin.Instance?.Server is { } server)
            {
                server.RebuildAndSendRoadNetwork(doc);
                server.RebuildZoneAnalysis(doc);
            }
            PaintFromCache();
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"[UrbanBridge] Dashboard recompute: {ex.Message}");
        }
    }

    private void OnZone(ZoneAnalysis a) => ScheduleApply(a, _server?.LatestRoadNetwork);
    private void OnRoad(RoadNetworkGraph g) => ScheduleApply(_server?.LatestZoneAnalysis, g);

    private void ScheduleApply(ZoneAnalysis? zones, RoadNetworkGraph? roads)
    {
        UiInvoke.Coalesce(ref _uiTimer, _uiGate, () => Apply(zones, roads));
    }

    private void Apply(ZoneAnalysis? zones, RoadNetworkGraph? roads)
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
            var greenPct = zones.TotalAreaSqm > 0 ? 100.0 * zones.TotalGreenAreaSqm / zones.TotalAreaSqm : 0;
            _greenLabel.Text = $"Green area: {zones.TotalGreenAreaSqm:F0} m² ({greenPct:F1}% of zones)";
            _areaByType.Text = zones.AreaByType.Count == 0
                ? "—"
                : string.Join("\n", zones.AreaByType.OrderBy(kv => kv.Key)
                    .Select(kv => $"{kv.Key,-12} {kv.Value,10:F0} m²"));
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
            var zoneKm2 = (zones?.TotalAreaSqm ?? 0) / 1_000_000.0;
            var roadKm = roads.Stats.TotalLengthM / 1000.0;
            _roadDensityLabel.Text = zoneKm2 > 1e-9
                ? $"Road density: {roadKm / zoneKm2:F2} km/km²"
                : "Road density: — (no zone area)";
        }

        var gfa = _server?.LatestMassingBuiltGfaSqm;
        _builtGfaLabel.Text = gfa is > 0
            ? $"Built floor area (massing): {gfa:F0} m²"
            : "Built floor area: — (run UrbanBridgeGenerateMassing)";
    }
}
