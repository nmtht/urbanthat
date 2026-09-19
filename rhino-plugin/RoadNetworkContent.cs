using Eto.Drawing;
using Eto.Forms;
using Rhino;

namespace UrbanBridge.Plugin;

/// <summary>Roads tab — custom UiButton Primary/Secondary.</summary>
public sealed class RoadNetworkContent : Panel
{
    private readonly Label _summaryLabel = new() { Text = "Road network: —", TextColor = Colors.White };
    private readonly TextArea _issuesText = new()
    {
        ReadOnly = true, Wrap = true, Height = 90,
        TextColor = Colors.White, BackgroundColor = UiTheme.InputBg,
    };
    private readonly Label _selectionLabel = new() { Text = "Selected curves: 0", TextColor = UiTheme.Muted };

    private readonly UiPresetBar _classPresets;
    private readonly UiPresetBar _dirPresets;
    private readonly UiPresetBar _streetPresets;
    private readonly UiValueSlider _lanesSlider;
    private readonly UiValueSlider _widthSlider;
    private readonly UiValueSlider _radiusSlider;
    private readonly UiValueSlider _medianSlider;
    private readonly UiValueSlider _greenStripSlider;
    private readonly UiValueSlider _parkingSlider;

    private BridgeServer? _server;
    private readonly object _uiGate = new();
    private System.Threading.Timer? _uiTimer;
    private RoadNetworkGraph? _pending;
    private bool _syncingUi;

    public RoadNetworkContent()
    {
        BackgroundColor = UiTheme.PanelBg;

        _classPresets = new UiPresetBar(RoadAttributeHelper.RoadClasses, 2);
        _classPresets.SelectedIndexChanged += (_, _) =>
        {
            if (_syncingUi) return;
            OnClassChanged();
        };

        _dirPresets = new UiPresetBar(RoadAttributeHelper.Directions, 0);
        _streetPresets = new UiPresetBar(RoadAttributeHelper.PresetNames, 0);

        _lanesSlider = new UiValueSlider("Lanes", 0, 6, 2) { Step = 1, FormatString = "0" };
        _widthSlider = new UiValueSlider("Width", 2, 30, 8) { Step = 0.5, Unit = " m", FormatString = "0.#" };
        _radiusSlider = new UiValueSlider("Corner radius", 0, 20, 4) { Step = 0.5, Unit = " m", FormatString = "0.#" };
        _medianSlider = new UiValueSlider("Median", 0, 8, 0) { Step = 0.5, Unit = " m", FormatString = "0.#" };
        _greenStripSlider = new UiValueSlider("Sidewalk green", 0, 4, 0) { Step = 0.25, Unit = " m", FormatString = "0.##" };
        _parkingSlider = new UiValueSlider("Parking strip", 0, 4, 0) { Step = 0.25, Unit = " m", FormatString = "0.##" };

        var initBtn = new UiButton("Init as road", UiButton.Style.Primary);
        initBtn.Click += (_, _) => InitSelected();
        var applyBtn = new UiButton("Apply attributes", UiButton.Style.Secondary);
        applyBtn.Click += (_, _) => ApplySelected();
        var presetBtn = new UiButton("Apply street preset", UiButton.Style.Secondary);
        presetBtn.Click += (_, _) => ApplyPreset();
        var genBtn = new UiButton("Generate Road Surfaces", UiButton.Style.Primary);
        genBtn.Click += (_, _) => GenerateSurfaces();
        genBtn.Width = 180;
        var refreshBtn = new UiButton("Refresh graph", UiButton.Style.Secondary);
        refreshBtn.Click += (_, _) => RebuildGraph();

        var attrLayout = new DynamicLayout { Padding = 8, Spacing = new Size(6, 6), BackgroundColor = UiTheme.CardBg };
        attrLayout.AddRow(_selectionLabel);
        attrLayout.AddRow(new Label { Text = "road_class", TextColor = Colors.White, Font = Fonts.Sans(8) });
        attrLayout.AddRow(_classPresets);
        attrLayout.AddRow(new Label { Text = "direction", TextColor = Colors.White, Font = Fonts.Sans(8) });
        attrLayout.AddRow(_dirPresets);
        attrLayout.AddRow(new Label { Text = "street preset", TextColor = Colors.White, Font = Fonts.Sans(8) });
        attrLayout.AddRow(_streetPresets);
        attrLayout.AddRow(_lanesSlider);
        attrLayout.AddRow(_widthSlider);
        attrLayout.AddRow(_radiusSlider);
        attrLayout.AddRow(_medianSlider);
        attrLayout.AddRow(_greenStripSlider);
        attrLayout.AddRow(_parkingSlider);
        attrLayout.AddRow(new TableLayout
        {
            Spacing = new Size(6, 0),
            Rows = { new TableRow(initBtn, applyBtn, presetBtn, null) },
        });

        var attrGroup = new GroupBox
        {
            Text = "Attributes (selected curves)",
            TextColor = Colors.White,
            BackgroundColor = UiTheme.CardBg,
            Content = attrLayout,
        };

        var root = new DynamicLayout
        {
            Padding = 12,
            Spacing = new Size(8, 6),
            BackgroundColor = UiTheme.PanelBg,
        };
        root.AddRow(new Label
        {
            Text = "Roads",
            Font = new Font(SystemFont.Bold, 13),
            TextColor = Colors.White,
        });
        root.AddRow(_summaryLabel);
        root.AddRow(new Label
        {
            Text = "Issues",
            Font = new Font(SystemFont.Bold, 10),
            TextColor = Colors.White,
        });
        root.AddRow(_issuesText);
        root.AddRow(attrGroup);
        root.AddRow(genBtn);
        root.AddRow(refreshBtn);

        Content = new Scrollable
        {
            Border = BorderType.None,
            BackgroundColor = UiTheme.PanelBg,
            Content = root,
        };

        UiTheme.ApplyDark(this);
        OnClassChanged();
    }

    public void AttachServer(BridgeServer? server)
    {
        if (_server is not null)
            _server.RoadNetworkUpdated -= OnUpdated;
        _server = server;
        if (_server is not null)
        {
            _server.RoadNetworkUpdated -= OnUpdated;
            _server.RoadNetworkUpdated += OnUpdated;
            if (_server.LatestRoadNetwork is { } g)
                OnUpdated(g);
        }
    }

    public void DetachServer()
    {
        if (_server is not null)
            _server.RoadNetworkUpdated -= OnUpdated;
        _server = null;
        UiInvoke.DisposeTimer(ref _uiTimer, _uiGate);
    }

    public void RebuildGraph()
    {
        try
        {
            if (RhinoDoc.ActiveDoc is { } doc && UrbanBridgePlugin.Instance?.Server is { } server)
                server.RebuildAndSendRoadNetwork(doc);
            else
                PaintFromCache();
            RefreshSelectionLabel();
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"[UrbanBridge] Road rebuild: {ex.Message}");
        }
    }

    public void PaintFromCache()
    {
        if (_server?.LatestRoadNetwork is { } g)
            ApplyUi(g);
        RefreshSelectionLabel();
    }

    public void RefreshSelectionLabel()
    {
        var doc = RhinoDoc.ActiveDoc;
        var n = doc is null ? 0 : RoadAttributeHelper.GetSelectedCurves(doc).Count;
        _selectionLabel.Text = $"Selected curves: {n}";
    }

    private void OnClassChanged()
    {
        var cls = _classPresets.SelectedItem;
        if (string.IsNullOrEmpty(cls) || !RoadAttributeHelper.ClassDefaults.TryGetValue(cls, out var d))
            return;
        _syncingUi = true;
        try
        {
            _lanesSlider.Value = d.Lanes;
            _widthSlider.Value = d.WidthM;
            _radiusSlider.Value = d.CornerRadiusM;
        }
        finally { _syncingUi = false; }
    }

    private void InitSelected()
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc is null) return;
        var curves = RoadAttributeHelper.GetSelectedCurves(doc);
        RefreshSelectionLabel();
        if (curves.Count == 0)
        {
            RhinoApp.WriteLine("[UrbanBridge] Select curves first.");
            return;
        }
        var cls = string.IsNullOrEmpty(_classPresets.SelectedItem) ? "local" : _classPresets.SelectedItem;
        var n = RoadAttributeHelper.InitAsRoad(doc, curves, cls);
        RhinoApp.WriteLine($"[UrbanBridge] Init as road: {n}");
        RebuildGraph();
    }

    private void ApplySelected()
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc is null) return;
        var curves = RoadAttributeHelper.GetSelectedCurves(doc);
        RefreshSelectionLabel();
        if (curves.Count == 0)
        {
            RhinoApp.WriteLine("[UrbanBridge] Select curves first.");
            return;
        }
        var cls = string.IsNullOrEmpty(_classPresets.SelectedItem) ? "local" : _classPresets.SelectedItem;
        var dir = string.Equals(_dirPresets.SelectedItem, "one_way", StringComparison.OrdinalIgnoreCase)
            ? "one_way" : "two_way";
        var n = RoadAttributeHelper.ApplyAttributes(
            doc, curves, cls,
            (int)Math.Round(_lanesSlider.Value),
            _widthSlider.Value,
            false,
            dir,
            _radiusSlider.Value,
            _medianSlider.Value,
            _greenStripSlider.Value,
            _parkingSlider.Value);
        RhinoApp.WriteLine($"[UrbanBridge] Applied road attrs to {n}");
        RebuildGraph();
    }

    private void ApplyPreset()
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc is null) return;
        var curves = RoadAttributeHelper.GetSelectedCurves(doc);
        if (curves.Count == 0) return;
        var name = _streetPresets.SelectedItem;
        if (string.IsNullOrEmpty(name)) return;
        var n = RoadAttributeHelper.ApplyPreset(doc, curves, name);
        RhinoApp.WriteLine($"[UrbanBridge] Applied preset {name} to {n}");

        if (RoadAttributeHelper.Presets.TryGetValue(name, out var p))
        {
            _syncingUi = true;
            try
            {
                _classPresets.SelectByName(p.Class);
                _dirPresets.SelectByName(p.Direction);
                _lanesSlider.Value = p.Lanes;
                _widthSlider.Value = p.WidthM;
                _radiusSlider.Value = p.CornerRadiusM;
                _medianSlider.Value = p.MedianM;
                _greenStripSlider.Value = p.SidewalkGreenM;
                _parkingSlider.Value = p.ParkingM;
            }
            finally { _syncingUi = false; }
        }

        RebuildGraph();
    }

    private void GenerateSurfaces()
    {
        try
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc is null) return;
            var server = UrbanBridgePlugin.Instance?.Server;
            if (server is null)
            {
                RhinoApp.WriteLine("[UrbanBridge] Server not running.");
                return;
            }
            server.RebuildAndSendRoadNetwork(doc);
            var graph = server.LatestRoadNetwork;
            if (graph is null || graph.Edges.Count == 0)
            {
                RhinoApp.WriteLine("[UrbanBridge] No road edges. Init curves as roads first.");
                return;
            }
            var gen = new RoadSurfaceGenerator(doc);
            var result = gen.Generate(doc, graph);
            server.MarkRoadSurfaceGenerated();
            RhinoApp.WriteLine(
                $"[UrbanBridge] Surfaces: created {result.CreatedCount}, deleted {result.DeletedCount}, " +
                $"failed links {result.FailedLinks}, hubs {result.FailedHubs}");
            ApplyUi(graph);
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"[UrbanBridge] Surface gen error: {ex.Message}");
        }
    }

    private void OnUpdated(RoadNetworkGraph graph)
    {
        _pending = graph;
        UiInvoke.Coalesce(ref _uiTimer, _uiGate, () =>
        {
            if (_pending is not null) ApplyUi(_pending);
        });
    }

    private void ApplyUi(RoadNetworkGraph graph)
    {
        _summaryLabel.Text =
            $"Edges: {graph.Edges.Count} · Nodes: {graph.Nodes.Count} · " +
            $"Length: {graph.Stats.TotalLengthM:F1} m · Components: {graph.Stats.ComponentCount}";
        _summaryLabel.TextColor = Colors.White;

        if (graph.Issues.Count == 0)
            _issuesText.Text = "No issues.";
        else
        {
            var lines = graph.Issues
                .OrderByDescending(i => i.Severity)
                .Select(i => $"[{i.Severity}] {i.Type}: {i.Message}");
            _issuesText.Text = UiInvoke.FormatCappedLines(lines);
        }

        RefreshSelectionLabel();
    }
}
