using Eto.Forms;
using Rhino;

namespace UrbanBridge.Rhino;

/// <summary>
/// Single plugin UI: tabs Roads | Zoning | Dashboard.
/// Only the active tab is refreshed from heavy analysis; others use cached snapshots.
/// </summary>
public sealed class UrbanBridgeMainContent : Panel
{
    private readonly RoadNetworkContent _roads = new();
    private readonly ZoneTabContent _zones = new();
    private readonly DashboardTabContent _dashboard = new();
    private readonly TabControl _tabs;

    public UrbanBridgeMainContent()
    {
        _tabs = new TabControl();
        _tabs.Pages.Add(new TabPage { Text = "Roads", Content = _roads });
        _tabs.Pages.Add(new TabPage { Text = "Zoning", Content = _zones });
        _tabs.Pages.Add(new TabPage { Text = "Dashboard", Content = _dashboard });
        _tabs.SelectedIndexChanged += (_, _) => RefreshActiveTab(fromCacheOnly: true);
        Content = _tabs;
    }

    public int SelectedTabIndex => _tabs.SelectedIndex;

    public void AttachServer(BridgeServer? server)
    {
        _roads.AttachServer(server);
        _zones.AttachServer(server);
        _dashboard.AttachServer(server);
    }

    public void DetachServer()
    {
        _roads.DetachServer();
        _zones.DetachServer();
        _dashboard.DetachServer();
    }

    /// <summary>Light open path: paint from cache, rebuild only the active tab if cache empty.</summary>
    public void RefreshActiveTab(bool fromCacheOnly = false)
    {
        switch (_tabs.SelectedIndex)
        {
            case 0:
                if (!fromCacheOnly || UrbanBridgePlugin.Instance?.Server?.LatestRoadNetwork is null)
                    _roads.RebuildGraph();
                else
                    _roads.PaintFromCache();
                _roads.RefreshSelectionLabel();
                break;
            case 1:
                if (!fromCacheOnly || UrbanBridgePlugin.Instance?.Server?.LatestZoneAnalysis is null)
                    _zones.Rebuild();
                else
                    _zones.PaintFromCache();
                break;
            case 2:
                // Dashboard never forces a full dual rebuild — only paints Latest*
                _dashboard.PaintFromCache();
                break;
        }
    }

    public void SelectTab(int index)
    {
        if (index >= 0 && index < _tabs.Pages.Count)
            _tabs.SelectedIndex = index;
    }
}

/// <summary>Zoning tab: metrics, issues, Init/Apply attributes.</summary>
public sealed class ZoneTabContent : Panel
{
    private readonly Label _summaryLabel = new() { Text = "Zones: —" };
    private readonly Label _staleLabel = new() { Text = "", TextColor = Eto.Drawing.Colors.DarkOrange };
    private readonly TextArea _zonesText = new() { ReadOnly = true, Wrap = true, Height = 120 };
    private readonly TextArea _issuesText = new() { ReadOnly = true, Wrap = true, Height = 100 };

    private readonly Label _selectionLabel = new() { Text = "Selected curves: 0" };
    private readonly DropDown _typeDrop = new();
    private readonly TextBox _farBox = new() { Text = "1.5", Width = 80 };
    private readonly TextBox _heightBox = new() { Text = "24", Width = 80 };
    private readonly TextBox _setbackBox = new() { Text = "3", Width = 80 };
    private readonly TextBox _greenBox = new() { Text = "0.25", Width = 80 };

    private BridgeServer? _server;
    private readonly object _uiGate = new();
    private System.Threading.Timer? _uiTimer;
    private ZoneAnalysis? _pending;

    public ZoneTabContent()
    {
        foreach (var t in ZoneTypeDefaults.ZoneTypes)
            _typeDrop.Items.Add(t);
        _typeDrop.SelectedIndex = 0;
        _typeDrop.SelectedIndexChanged += (_, _) => OnTypeChanged();

        var initBtn = new Button { Text = "Init as zone (UserText + layer Zones)" };
        initBtn.Click += (_, _) => InitSelected();

        var applyBtn = new Button { Text = "Apply attributes to selection" };
        applyBtn.Click += (_, _) => ApplySelected();

        var readBtn = new Button { Text = "Read from selection" };
        readBtn.Click += (_, _) => ReadSelection();

        var refreshBtn = new Button { Text = "Refresh zones" };
        refreshBtn.Click += (_, _) => Rebuild();

        var attrGroup = new GroupBox
        {
            Text = "Attributes (selected closed curves)",
            Content = new StackLayout
            {
                Padding = 6,
                Spacing = 4,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    _selectionLabel,
                    new TableLayout
                    {
                        Spacing = new Eto.Drawing.Size(6, 4),
                        Rows =
                        {
                            new TableRow(new Label { Text = "zone_type" }, _typeDrop),
                            new TableRow(new Label { Text = "far" }, _farBox),
                            new TableRow(new Label { Text = "height_max (m)" }, _heightBox),
                            new TableRow(new Label { Text = "setback_m" }, _setbackBox),
                            new TableRow(new Label { Text = "green_ratio" }, _greenBox),
                        },
                    },
                    new StackLayout
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 6,
                        Items = { initBtn, applyBtn },
                    },
                    readBtn,
                },
            },
        };

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
                    _summaryLabel,
                    _staleLabel,
                    new Label { Text = "Zones" },
                    _zonesText,
                    new Label { Text = "Issues" },
                    _issuesText,
                    attrGroup,
                    refreshBtn,
                },
            },
        };
    }

    public void AttachServer(BridgeServer? server)
    {
        if (_server is not null)
            _server.ZoneAnalysisUpdated -= OnUpdated;
        _server = server;
        if (_server is not null)
        {
            _server.ZoneAnalysisUpdated -= OnUpdated;
            _server.ZoneAnalysisUpdated += OnUpdated;
            if (_server.LatestZoneAnalysis is { } a)
                OnUpdated(a);
        }
    }

    public void DetachServer()
    {
        if (_server is not null)
            _server.ZoneAnalysisUpdated -= OnUpdated;
        _server = null;
        UiInvoke.DisposeTimer(ref _uiTimer, _uiGate);
    }

    public void Rebuild()
    {
        try
        {
            if (RhinoDoc.ActiveDoc is { } doc && UrbanBridgePlugin.Instance?.Server is { } server)
                server.RebuildZoneAnalysis(doc);
            else
                PaintFromCache();
            RefreshSelectionLabel();
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"[UrbanBridge] Zone tab rebuild: {ex.Message}");
        }
    }

    public void PaintFromCache()
    {
        if (_server?.LatestZoneAnalysis is { } a)
            ApplyUi(a);
        RefreshSelectionLabel();
    }

    private void RefreshSelectionLabel()
    {
        var doc = RhinoDoc.ActiveDoc;
        var n = doc is null ? 0 : ZoneAttributeHelper.GetSelectedCurves(doc).Count;
        _selectionLabel.Text = $"Selected curves: {n}";
    }

    private void OnTypeChanged()
    {
        var type = SelectedType();
        var d = ZoneTypeDefaults.Get(type);
        _farBox.Text = Format(d.Far);
        _heightBox.Text = Format(d.HeightMaxM);
        _setbackBox.Text = Format(ZoneTypeDefaults.DefaultSetbackM);
        _greenBox.Text = Format(d.GreenRatio);
    }

    private string SelectedType()
    {
        if (_typeDrop.SelectedIndex >= 0 && _typeDrop.SelectedIndex < ZoneTypeDefaults.ZoneTypes.Length)
            return ZoneTypeDefaults.ZoneTypes[_typeDrop.SelectedIndex];
        return "residential";
    }

    private void InitSelected()
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc is null) return;
        var curves = ZoneAttributeHelper.GetSelectedCurves(doc);
        RefreshSelectionLabel();
        if (curves.Count == 0)
        {
            RhinoApp.WriteLine("[UrbanBridge] Select one or more closed curves first.");
            return;
        }

        var n = ZoneAttributeHelper.InitAsZone(doc, curves, SelectedType());
        RhinoApp.WriteLine($"[UrbanBridge] Init as zone: {n} curve(s) → layer Zones + default UserText.");
        Rebuild();
        ReadSelection();
    }

    private void ApplySelected()
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc is null) return;
        var curves = ZoneAttributeHelper.GetSelectedCurves(doc);
        RefreshSelectionLabel();
        if (curves.Count == 0)
        {
            RhinoApp.WriteLine("[UrbanBridge] Select one or more curves first.");
            return;
        }

        var type = SelectedType();
        var far = ParseBox(_farBox, ZoneTypeDefaults.Get(type).Far);
        var height = ParseBox(_heightBox, ZoneTypeDefaults.Get(type).HeightMaxM);
        var setback = ParseBox(_setbackBox, ZoneTypeDefaults.DefaultSetbackM);
        var green = ParseBox(_greenBox, ZoneTypeDefaults.Get(type).GreenRatio);

        var n = ZoneAttributeHelper.ApplyAttributes(doc, curves, type, far, height, setback, green);
        RhinoApp.WriteLine($"[UrbanBridge] Applied zone attrs to {n}: type={type}, far={far}, h={height}, setback={setback}, green={green}");
        Rebuild();
    }

    private void ReadSelection()
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc is null) return;
        var curves = ZoneAttributeHelper.GetSelectedCurves(doc);
        RefreshSelectionLabel();
        var data = ZoneAttributeHelper.ReadFirst(curves);
        if (data is null) return;

        var (type, far, height, setback, green) = data.Value;
        var idx = Array.FindIndex(ZoneTypeDefaults.ZoneTypes, t =>
            t.Equals(type, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0) _typeDrop.SelectedIndex = idx;
        _farBox.Text = Format(far);
        _heightBox.Text = Format(height);
        _setbackBox.Text = Format(setback);
        _greenBox.Text = Format(green);
    }

    private void OnUpdated(ZoneAnalysis analysis)
    {
        _pending = analysis;
        UiInvoke.Coalesce(ref _uiTimer, _uiGate, () =>
        {
            var data = _pending;
            if (data is not null)
                ApplyUi(data);
        });
    }

    private void ApplyUi(ZoneAnalysis analysis)
    {
        _summaryLabel.Text =
            $"Zones: {analysis.Zones.Count} · area {analysis.TotalAreaSqm:F0} m² · " +
            $"pop {analysis.TotalPopulation:F0} · jobs {analysis.TotalJobs:F0}";

        if (analysis.RoadSurfacesStale)
            _staleLabel.Text = "⚠ Road surfaces may be outdated — re-run Generate Road Surfaces.";
        else if (analysis.LastRoadSurfaceGenUtc is null)
            _staleLabel.Text = "Road surfaces not generated yet (needed for zone_no_road_access).";
        else
            _staleLabel.Text = "";

        if (analysis.Zones.Count == 0)
        {
            _zonesText.Text = "No closed curves on Zones. Select curves → Init as zone.";
        }
        else
        {
            var lines = analysis.Zones.Select(z =>
            {
                analysis.MetricsById.TryGetValue(z.RhinoObjectId, out var m);
                return $"{z.ZoneType,-12} {(m?.AreaSqm ?? 0),8:F0} m²  build {(m?.BuildableAreaSqm ?? 0),8:F0}  pop {(m?.EstimatedPopulation ?? 0),6:F0}  front {(m?.RoadFrontageM ?? 0),5:F0} m";
            });
            _zonesText.Text = UiInvoke.FormatCappedLines(lines, 50);
        }

        if (analysis.Issues.Count == 0)
        {
            _issuesText.Text = "No issues.";
        }
        else
        {
            var lines = analysis.Issues
                .OrderByDescending(i => i.Severity)
                .Select(i => $"[{i.Severity}] {i.Type}: {i.Message}");
            _issuesText.Text = UiInvoke.FormatCappedLines(lines);
        }

        RefreshSelectionLabel();
    }

    private static double ParseBox(TextBox box, double fallback) =>
        double.TryParse(box.Text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static string Format(double v) =>
        v.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Dashboard tab — paints from cached Latest* only (no forced dual rebuild).</summary>
public sealed class DashboardTabContent : Panel
{
    private readonly Label _popLabel = new() { Text = "Population: —" };
    private readonly Label _jobsLabel = new() { Text = "Jobs: —" };
    private readonly Label _greenLabel = new() { Text = "Green: —" };
    private readonly Label _roadDensityLabel = new() { Text = "Road density: —" };
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
    }
}
