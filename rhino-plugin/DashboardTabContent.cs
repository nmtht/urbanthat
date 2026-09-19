using Eto.Drawing;
using Eto.Forms;
using Rhino;
using Rhino.Geometry;

namespace UrbanBridge.Plugin;

/// <summary>
/// Project dashboard — forced dark theme, white text, custom buttons.
/// Balance relative to project boundary.
/// </summary>
public sealed class DashboardTabContent : Panel
{
    private readonly Label _boundaryLabel = new() { Text = "Boundary: not set", TextColor = Colors.White };
    private readonly Label _boundaryAreaLabel = new() { Text = "Boundary area: —", TextColor = UiTheme.Muted };
    private readonly CheckBox _autoUpdate = new()
    {
        Text = "Auto-update ALL geometry",
        Checked = false,
        TextColor = Colors.White,
    };
    private readonly Label _popLabel = new() { Text = "Population: —", TextColor = Colors.White };
    private readonly Label _jobsLabel = new() { Text = "Jobs: —", TextColor = Colors.White };
    private readonly Label _greenLabel = new() { Text = "Green: —", TextColor = Colors.White };
    private readonly Label _roadDensityLabel = new() { Text = "Road density: —", TextColor = Colors.White };
    private readonly Label _builtGfaLabel = new() { Text = "Built floor area: —", TextColor = Colors.White };
    private readonly Label _staleLabel = new() { Text = "", TextColor = UiTheme.Danger };
    private readonly Label _balanceLabel = new() { Text = "Balance: —", TextColor = Colors.White };
    private readonly Label _unzonedLabel = new() { Text = "", TextColor = UiTheme.Muted };
    private readonly Drawable _chart = new() { Size = new Size(320, 150), BackgroundColor = UiTheme.CardBg };
    private readonly TextArea _areaByType = new()
    {
        ReadOnly = true, Wrap = true, Height = 90,
        TextColor = Colors.White, BackgroundColor = UiTheme.InputBg,
    };
    private readonly TextArea _roadStats = new()
    {
        ReadOnly = true, Wrap = true, Height = 70,
        TextColor = Colors.White, BackgroundColor = UiTheme.InputBg,
    };
    private readonly Label _xlsHint = new()
    {
        Text = "Export to XLS — planned.",
        TextColor = UiTheme.Muted,
    };

    private BridgeServer? _server;
    private readonly object _uiGate = new();
    private System.Threading.Timer? _uiTimer;
    private Dictionary<string, double> _chartData = new(StringComparer.OrdinalIgnoreCase);
    private double _boundaryAreaSqm;

    public DashboardTabContent()
    {
        BackgroundColor = UiTheme.PanelBg;
        _autoUpdate.Checked = PluginSettings.AutoUpdateGeometry;
        _autoUpdate.CheckedChanged += (_, _) =>
            PluginSettings.AutoUpdateGeometry = _autoUpdate.Checked == true;

        var setBoundary = new UiButton("Set from selection", UiButton.Style.Primary);
        setBoundary.Click += (_, _) => SetBoundaryFromSelection();
        var clearBoundary = new UiButton("Clear", UiButton.Style.Secondary);
        clearBoundary.Click += (_, _) =>
        {
            PluginSettings.ProjectBoundaryId = null;
            ForceRecompute();
        };
        var refresh = new UiButton("Refresh", UiButton.Style.Secondary);
        refresh.Click += (_, _) => ForceRecompute();

        _chart.Paint += OnChartPaint;

        var title = new Label
        {
            Text = "Project Dashboard",
            Font = new Font(SystemFont.Bold, 13),
            TextColor = Colors.White,
        };

        var boundaryBox = new GroupBox
        {
            Text = "Territory boundary (required for balance)",
            TextColor = Colors.White,
            BackgroundColor = UiTheme.CardBg,
            Content = new DynamicLayout
            {
                Padding = 8,
                Spacing = new Size(6, 4),
                BackgroundColor = UiTheme.CardBg,
                Rows =
                {
                    new Label
                    {
                        Text = "All KPIs & land-use balance use this closed curve as 100%.",
                        TextColor = UiTheme.Muted,
                    },
                    _boundaryLabel,
                    _boundaryAreaLabel,
                    new TableLayout
                    {
                        Spacing = new Size(6, 0),
                        Rows = { new TableRow(setBoundary, clearBoundary, null) },
                    },
                },
            },
        };

        var kpiLayout = new DynamicLayout { Spacing = new Size(4, 2), BackgroundColor = UiTheme.PanelBg };
        kpiLayout.AddRow(_popLabel);
        kpiLayout.AddRow(_jobsLabel);
        kpiLayout.AddRow(_greenLabel);
        kpiLayout.AddRow(_roadDensityLabel);
        kpiLayout.AddRow(_builtGfaLabel);

        var root = new DynamicLayout
        {
            Padding = 12,
            Spacing = new Size(8, 8),
            DefaultSpacing = new Size(6, 4),
            BackgroundColor = UiTheme.PanelBg,
        };
        root.AddRow(title);
        root.AddRow(_autoUpdate);
        root.AddRow(new Label
        {
            Text = "On: road/zone edits regenerate surfaces, proxies, massing, courtyards.",
            TextColor = UiTheme.Muted,
        });
        root.AddRow(boundaryBox);
        root.AddRow(_staleLabel);
        root.AddRow(new Label
        {
            Text = "Key metrics",
            Font = new Font(SystemFont.Bold, 10),
            TextColor = Colors.White,
        });
        root.AddRow(kpiLayout);
        root.AddRow(new Label
        {
            Text = "Land-use balance (of boundary)",
            Font = new Font(SystemFont.Bold, 10),
            TextColor = Colors.White,
        });
        root.AddRow(_balanceLabel);
        root.AddRow(_unzonedLabel);
        root.AddRow(_chart);
        root.AddRow(new Label { Text = "Area by zone_type (inside boundary)", TextColor = Colors.White });
        root.AddRow(_areaByType);
        root.AddRow(new Label { Text = "Road network (inside boundary)", TextColor = Colors.White });
        root.AddRow(_roadStats);
        root.AddRow(refresh);
        root.AddRow(_xlsHint);

        Content = new Scrollable
        {
            Border = BorderType.None,
            BackgroundColor = UiTheme.PanelBg,
            Content = root,
        };

        UiTheme.ApplyDark(this);
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
                RhinoApp.WriteLine($"[UrbanBridge] Project boundary set: {obj.Id.ToString()[..8]}…");
                ForceRecompute();
                return;
            }
        }
        RhinoApp.WriteLine("[UrbanBridge] Select a closed curve first.");
    }

    private void UpdateBoundaryLabel()
    {
        _boundaryAreaSqm = ComputeBoundaryAreaSqm();
        if (PluginSettings.ProjectBoundaryId is { } id)
        {
            _boundaryLabel.Text = $"Boundary: {id.ToString()[..8]}…";
            _boundaryLabel.TextColor = Colors.White;
            _boundaryAreaLabel.Text = _boundaryAreaSqm > 0
                ? $"Boundary area: {_boundaryAreaSqm:F0} m² ({_boundaryAreaSqm / 10_000.0:F2} ha)"
                : "Boundary area: — (invalid curve)";
        }
        else
        {
            _boundaryLabel.Text = "Boundary: not set";
            _boundaryLabel.TextColor = Colors.White;
            _boundaryAreaLabel.Text = "Set a closed curve — balance % uses it as 100%.";
            _boundaryAreaSqm = 0;
        }
    }

    private static double ComputeBoundaryAreaSqm()
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc is null || PluginSettings.ProjectBoundaryId is not { } id) return 0;
        var obj = doc.Objects.FindId(id);
        if (obj?.Geometry is not Curve c || !c.IsClosed) return 0;
        var amp = AreaMassProperties.Compute(c);
        if (amp is null) return 0;
        var scale = RhinoMath.UnitScale(doc.ModelUnitSystem, UnitSystem.Meters);
        return amp.Area * scale * scale;
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
        var denom = _boundaryAreaSqm;

        if (zones is null)
        {
            _popLabel.Text = "Population: —";
            _jobsLabel.Text = "Jobs: —";
            _greenLabel.Text = "Green: —";
            _balanceLabel.Text = "Balance: —";
            _unzonedLabel.Text = "";
            _areaByType.Text = "No zone data.";
            _chartData = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            _chart.Invalidate();
        }
        else
        {
            _popLabel.Text = $"Population (est.): {zones.TotalPopulation:F0}";
            _jobsLabel.Text = $"Jobs (est.): {zones.TotalJobs:F0}";

            if (denom <= 0)
            {
                _greenLabel.Text = $"Green (zones): {zones.TotalGreenAreaSqm:F0} m² — set boundary for %";
                _balanceLabel.Text = "Balance: set project boundary to compute % of territory";
                _unzonedLabel.Text = "Without a boundary, percentages of zone sum only (not territory).";
                denom = zones.TotalAreaSqm;
            }
            else
            {
                var greenPct = 100.0 * zones.TotalGreenAreaSqm / denom;
                _greenLabel.Text =
                    $"Green: {zones.TotalGreenAreaSqm:F0} m² ({greenPct:F1}% of boundary)";

                var res = zones.AreaByType.GetValueOrDefault("residential");
                var com = zones.AreaByType.GetValueOrDefault("commercial") +
                          zones.AreaByType.GetValueOrDefault("mixed_use");
                var ind = zones.AreaByType.GetValueOrDefault("industrial");
                var greenZone = zones.AreaByType.GetValueOrDefault("green") +
                                zones.AreaByType.GetValueOrDefault("public");
                var zonedSum = zones.TotalAreaSqm;
                var unzoned = Math.Max(0, denom - zonedSum);

                _balanceLabel.Text =
                    $"Of boundary: live {Pct(res, denom)}% · work {Pct(com, denom)}% · " +
                    $"industry {Pct(ind, denom)}% · green/public {Pct(greenZone, denom)}%";
                _unzonedLabel.Text =
                    $"Unzoned residual: {unzoned:F0} m² ({Pct(unzoned, denom)}% of boundary) · " +
                    $"zoned {zonedSum:F0} m²";
            }

            _chartData = new Dictionary<string, double>(zones.AreaByType, StringComparer.OrdinalIgnoreCase);
            if (denom > 0 && zones.TotalAreaSqm < denom)
                _chartData["unzoned"] = Math.Max(0, denom - zones.TotalAreaSqm);
            _chart.Invalidate();

            _areaByType.Text = zones.AreaByType.Count == 0
                ? "—"
                : string.Join("\n", zones.AreaByType.OrderBy(kv => kv.Key)
                    .Select(kv =>
                    {
                        var p = denom > 0 ? 100.0 * kv.Value / denom : 0;
                        return $"{kv.Key,-12} {kv.Value,10:F0} m²  ({p:F1}% of boundary)";
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
            var boundaryKm2 = denom / 1_000_000.0;
            var roadKm = roads.Stats.TotalLengthM / 1000.0;
            _roadDensityLabel.Text = boundaryKm2 > 1e-9
                ? $"Road density: {roadKm / boundaryKm2:F2} km/km² (vs boundary)"
                : "Road density: — (set boundary)";
        }

        var gfa = _server?.LatestMassingBuiltGfaSqm;
        _builtGfaLabel.Text = gfa is > 0
            ? $"Built floor area (massing): {gfa:F0} m²"
            : "Built floor area: — (Generate massing on Zoning tab)";
    }

    private void OnChartPaint(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(UiTheme.CardBg);
        if (_chartData.Count == 0)
        {
            g.DrawText(Fonts.Sans(9), Colors.White, 8, 8, "No data — set boundary & zones");
            return;
        }

        var items = _chartData.OrderByDescending(kv => kv.Value).Take(7).ToList();
        var max = items.Max(kv => kv.Value);
        if (max <= 0) return;

        var pad = 8f;
        var labelW = 72f;
        var rowH = Math.Min(20f, (e.ClipRectangle.Height - pad * 2) / Math.Max(1, items.Count));
        var barMax = e.ClipRectangle.Width - pad * 2 - labelW - 48;

        for (var i = 0; i < items.Count; i++)
        {
            var kv = items[i];
            var y = pad + i * rowH;
            var barW = (float)(barMax * (kv.Value / max));
            var color = ColorForType(kv.Key);
            g.FillRectangle(color, pad + labelW, y + 2, Math.Max(2, barW), rowH - 4);
            g.DrawText(Fonts.Sans(8), Colors.White, pad, y + 2, Truncate(kv.Key, 10));
            var pct = _boundaryAreaSqm > 0 ? 100.0 * kv.Value / _boundaryAreaSqm : 0;
            g.DrawText(Fonts.Sans(8), Colors.White, pad + labelW + barW + 4, y + 2, $"{pct:F0}%");
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
        "unzoned" => Color.FromArgb(100, 100, 105),
        _ => Color.FromArgb(160, 180, 200),
    };

    private static string Truncate(string s, int n) =>
        s.Length <= n ? s : s[..(n - 1)] + "…";

    private static double Pct(double part, double total) =>
        total > 0 ? 100.0 * part / total : 0;
}
