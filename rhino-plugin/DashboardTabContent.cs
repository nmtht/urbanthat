using Eto.Drawing;
using Eto.Forms;
using Rhino;
using Rhino.Geometry;

namespace UrbanBridge.Plugin;

/// <summary>Project dashboard: KPIs, bar chart, boundary scope, auto-update toggle.</summary>
public sealed class DashboardTabContent : Panel
{
    private readonly Label _boundaryLabel = new() { Text = "Boundary: (entire model)" };
    private readonly CheckBox _autoUpdate = new()
    {
        Text = "Auto-update ALL geometry (roads, zones, massing, courtyards)",
        Checked = false,
    };
    private readonly Label _popLabel = new() { Text = "Population: —" };
    private readonly Label _jobsLabel = new() { Text = "Jobs: —" };
    private readonly Label _greenLabel = new() { Text = "Green: —" };
    private readonly Label _roadDensityLabel = new() { Text = "Road density: —" };
    private readonly Label _builtGfaLabel = new() { Text = "Built floor area: —" };
    private readonly Label _staleLabel = new() { Text = "", TextColor = Colors.DarkOrange };
    private readonly Label _balanceLabel = new() { Text = "Balance: —" };
    private readonly Drawable _chart = new() { Size = new Size(320, 140), BackgroundColor = Colors.White };
    private readonly TextArea _areaByType = new() { ReadOnly = true, Wrap = true, Height = 90 };
    private readonly TextArea _roadStats = new() { ReadOnly = true, Wrap = true, Height = 70 };
    private readonly Label _xlsHint = new()
    {
        Text = "Export to XLS — planned (link dashboard metrics to a spreadsheet).",
        TextColor = Colors.Gray,
    };

    private BridgeServer? _server;
    private readonly object _uiGate = new();
    private System.Threading.Timer? _uiTimer;
    private Dictionary<string, double> _chartData = new(StringComparer.OrdinalIgnoreCase);

    public DashboardTabContent()
    {
        _autoUpdate.Checked = PluginSettings.AutoUpdateGeometry;
        _autoUpdate.CheckedChanged += (_, _) =>
            PluginSettings.AutoUpdateGeometry = _autoUpdate.Checked == true;

        var setBoundary = new Button { Text = "Set project boundary from selection" };
        setBoundary.Click += (_, _) => SetBoundaryFromSelection();
        var clearBoundary = new Button { Text = "Clear boundary" };
        clearBoundary.Click += (_, _) =>
        {
            PluginSettings.ProjectBoundaryId = null;
            _boundaryLabel.Text = "Boundary: (entire model)";
            ForceRecompute();
        };

        var refresh = new Button { Text = "Refresh (recompute)" };
        refresh.Click += (_, _) => ForceRecompute();

        _chart.Paint += OnChartPaint;

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
                    new Label { Text = "Project Dashboard", Font = new Font(SystemFont.Bold, 12) },
                    _autoUpdate,
                    new Label
                    {
                        Text = "When on: move road/zone curve → surfaces, proxies, buildings & green update.",
                        TextColor = Colors.Gray,
                    },
                    new GroupBox
                    {
                        Text = "Project boundary",
                        Content = new StackLayout
                        {
                            Padding = 6, Spacing = 4,
                            Items =
                            {
                                _boundaryLabel,
                                new StackLayout
                                {
                                    Orientation = Orientation.Horizontal, Spacing = 6,
                                    Items = { setBoundary, clearBoundary },
                                },
                                new Label
                                {
                                    Text = "Select a closed curve → Set boundary. Stats filter zones/roads inside it.",
                                    TextColor = Colors.Gray,
                                },
                            },
                        },
                    },
                    _staleLabel,
                    _popLabel,
                    _jobsLabel,
                    _greenLabel,
                    _roadDensityLabel,
                    _builtGfaLabel,
                    _balanceLabel,
                    new Label { Text = "Land-use balance" },
                    _chart,
                    new Label { Text = "Area by zone_type" },
                    _areaByType,
                    new Label { Text = "Road network" },
                    _roadStats,
                    refresh,
                    _xlsHint,
                },
            },
        };

        UpdateBoundaryLabel();
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

    private void SetBoundaryFromSelection()
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc is null) return;
        foreach (var obj in doc.Objects.GetSelectedObjects(false, false))
        {
            if (obj?.Geometry is Curve c && c.IsClosed)
            {
                PluginSettings.ProjectBoundaryId = obj.Id;
                UpdateBoundaryLabel();
                RhinoApp.WriteLine($"[UrbanBridge] Project boundary set: {obj.Id.ToString()[..8]}…");
                ForceRecompute();
                return;
            }
        }
        RhinoApp.WriteLine("[UrbanBridge] Select a closed curve first.");
    }

    private void UpdateBoundaryLabel()
    {
        if (PluginSettings.ProjectBoundaryId is { } id)
            _boundaryLabel.Text = $"Boundary: {id.ToString()[..8]}…";
        else
            _boundaryLabel.Text = "Boundary: (entire model)";
    }

    private void OnZone(ZoneAnalysis a) => ScheduleApply(a, _server?.LatestRoadNetwork);
    private void OnRoad(RoadNetworkGraph g) => ScheduleApply(_server?.LatestZoneAnalysis, g);

    private void ScheduleApply(ZoneAnalysis? zones, RoadNetworkGraph? roads)
    {
        UiInvoke.Coalesce(ref _uiTimer, _uiGate, () => Apply(zones, roads));
    }

    private void Apply(ZoneAnalysis? zones, RoadNetworkGraph? roads)
    {
        UpdateBoundaryLabel();

        if (zones is null)
        {
            _popLabel.Text = "Population: —";
            _jobsLabel.Text = "Jobs: —";
            _greenLabel.Text = "Green: —";
            _balanceLabel.Text = "Balance: —";
            _areaByType.Text = "No zone data.";
            _chartData = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            _chart.Invalidate();
        }
        else
        {
            _popLabel.Text = $"Population (est.): {zones.TotalPopulation:F0}";
            _jobsLabel.Text = $"Jobs (est.): {zones.TotalJobs:F0}";
            var greenPct = zones.TotalAreaSqm > 0 ? 100.0 * zones.TotalGreenAreaSqm / zones.TotalAreaSqm : 0;
            _greenLabel.Text = $"Green area: {zones.TotalGreenAreaSqm:F0} m² ({greenPct:F1}% of zones)";

            var res = zones.AreaByType.GetValueOrDefault("residential");
            var com = zones.AreaByType.GetValueOrDefault("commercial") +
                      zones.AreaByType.GetValueOrDefault("mixed_use");
            var ind = zones.AreaByType.GetValueOrDefault("industrial");
            _balanceLabel.Text =
                $"Balance: live {Pct(res, zones.TotalAreaSqm)}% · work {Pct(com, zones.TotalAreaSqm)}% · " +
                $"industry {Pct(ind, zones.TotalAreaSqm)}% · green {greenPct:F0}%";

            _chartData = new Dictionary<string, double>(zones.AreaByType, StringComparer.OrdinalIgnoreCase);
            _chart.Invalidate();

            _areaByType.Text = zones.AreaByType.Count == 0
                ? "—"
                : string.Join("\n", zones.AreaByType.OrderBy(kv => kv.Key)
                    .Select(kv =>
                    {
                        var p = zones.TotalAreaSqm > 0 ? 100.0 * kv.Value / zones.TotalAreaSqm : 0;
                        return $"{kv.Key,-12} {kv.Value,10:F0} m²  ({p:F1}%)";
                    }));

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
            : "Built floor area: — (Generate massing on Zoning tab)";
    }

    private void OnChartPaint(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Colors.White);
        if (_chartData.Count == 0)
        {
            g.DrawText(Fonts.Sans(9), Colors.Gray, 8, 8, "No zone area data");
            return;
        }

        var items = _chartData.OrderByDescending(kv => kv.Value).Take(6).ToList();
        var max = items.Max(kv => kv.Value);
        if (max <= 0) return;

        var pad = 8f;
        var labelW = 72f;
        var rowH = Math.Min(22f, (e.ClipRectangle.Height - pad * 2) / Math.Max(1, items.Count));
        var barMax = e.ClipRectangle.Width - pad * 2 - labelW - 40;

        for (var i = 0; i < items.Count; i++)
        {
            var kv = items[i];
            var y = pad + i * rowH;
            var barW = (float)(barMax * (kv.Value / max));
            var color = ColorForType(kv.Key);
            g.FillRectangle(color, pad + labelW, y + 2, Math.Max(2, barW), rowH - 4);
            g.DrawText(Fonts.Sans(8), Colors.Black, pad, y + 2, Truncate(kv.Key, 10));
            g.DrawText(Fonts.Sans(8), Colors.DimGray, pad + labelW + barW + 4, y + 2, $"{kv.Value:F0}");
        }
    }

    private static Color ColorForType(string t) => t.ToLowerInvariant() switch
    {
        "residential" => Color.FromArgb(180, 220, 140),
        "commercial" => Color.FromArgb(230, 170, 120),
        "mixed_use" => Color.FromArgb(200, 180, 220),
        "industrial" => Color.FromArgb(170, 170, 180),
        "green" => Color.FromArgb(90, 160, 90),
        "public" => Color.FromArgb(120, 170, 210),
        _ => Color.FromArgb(160, 180, 200),
    };

    private static string Truncate(string s, int n) =>
        s.Length <= n ? s : s[..(n - 1)] + "…";

    private static double Pct(double part, double total) =>
        total > 0 ? 100.0 * part / total : 0;
}
