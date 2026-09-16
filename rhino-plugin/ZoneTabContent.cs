using Eto.Forms;
using Rhino;

namespace UrbanBridge.Plugin;

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
                Padding = 6, Spacing = 4,
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
                        Orientation = Orientation.Horizontal, Spacing = 6,
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
                Padding = 10, Spacing = 6,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    _summaryLabel, _staleLabel,
                    new Label { Text = "Zones" }, _zonesText,
                    new Label { Text = "Issues" }, _issuesText,
                    attrGroup, refreshBtn,
                },
            },
        };
    }

    public void AttachServer(BridgeServer? server)
    {
        if (_server is not null) _server.ZoneAnalysisUpdated -= OnUpdated;
        _server = server;
        if (_server is not null)
        {
            _server.ZoneAnalysisUpdated -= OnUpdated;
            _server.ZoneAnalysisUpdated += OnUpdated;
            if (_server.LatestZoneAnalysis is { } a) OnUpdated(a);
        }
    }

    public void DetachServer()
    {
        if (_server is not null) _server.ZoneAnalysisUpdated -= OnUpdated;
        _server = null;
        UiInvoke.DisposeTimer(ref _uiTimer, _uiGate);
    }

    public void Rebuild()
    {
        try
        {
            if (RhinoDoc.ActiveDoc is { } doc && UrbanBridgePlugin.Instance?.Server is { } server)
                server.RebuildZoneAnalysis(doc);
            else PaintFromCache();
            RefreshSelectionLabel();
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"[UrbanBridge] Zone tab rebuild: {ex.Message}");
        }
    }

    public void PaintFromCache()
    {
        if (_server?.LatestZoneAnalysis is { } a) ApplyUi(a);
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
        RhinoApp.WriteLine($"[UrbanBridge] Init as zone: {n} curve(s).");
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
        RhinoApp.WriteLine($"[UrbanBridge] Applied zone attrs to {n}");
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
            if (_pending is not null) ApplyUi(_pending);
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
            _zonesText.Text = "No closed curves on Zones. Select curves → Init as zone.";
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
            _issuesText.Text = "No issues.";
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
