using Eto.Forms;
using Rhino;

namespace UrbanBridge.Rhino;

/// <summary>Roads tab: stats, attributes, presets, Generate Road Surfaces.</summary>
public sealed class RoadNetworkContent : Panel
{
    private readonly Label _totalLengthLabel = new() { Text = "Total length: —" };
    private readonly Label _intersectionsLabel = new() { Text = "Intersections: —" };
    private readonly Label _deadEndsLabel = new() { Text = "Dead ends: —" };
    private readonly Label _componentsLabel = new() { Text = "Components: —" };
    private readonly Label _byClassLabel = new() { Text = "By class: —" };
    private readonly TextArea _issuesText = new()
    {
        ReadOnly = true,
        Wrap = true,
        Height = 110,
        Text = "No issues yet.\nAdd curves on layer Roads.",
    };

    private readonly Label _selectionLabel = new() { Text = "Selected curves: 0" };
    private readonly DropDown _presetDrop = new();
    private readonly DropDown _classDropDown = new();
    private readonly NumericStepper _lanesStepper = new()
    {
        MinValue = 0, MaxValue = 16, DecimalPlaces = 0, Value = 2, Width = 80,
    };
    private readonly TextBox _widthBox = new() { Text = "8", Width = 80 };
    private readonly CheckBox _terminalCheck = new() { Text = "is_terminal", Checked = false };
    private readonly DropDown _directionDrop = new();
    private readonly TextBox _cornerRadiusBox = new() { Text = "4", Width = 80 };
    private readonly TextBox _medianBox = new() { Text = "0", Width = 80 };
    private readonly TextBox _sidewalkGreenBox = new() { Text = "0", Width = 80 };
    private readonly TextBox _parkingBox = new() { Text = "0", Width = 80 };
    private readonly Label _generateStatusLabel = new() { Text = "" };

    private BridgeServer? _server;
    private readonly object _uiGate = new();
    private System.Threading.Timer? _uiTimer;
    private RoadNetworkGraph? _pendingGraph;

    public RoadNetworkContent()
    {
        foreach (var p in RoadAttributeHelper.PresetNames)
            _presetDrop.Items.Add(p);
        _presetDrop.SelectedIndex = 1; // residential

        foreach (var c in RoadAttributeHelper.RoadClasses)
            _classDropDown.Items.Add(c);
        _classDropDown.SelectedIndex = 2;

        foreach (var d in RoadAttributeHelper.Directions)
            _directionDrop.Items.Add(d);
        _directionDrop.SelectedIndex = 0;

        _classDropDown.SelectedIndexChanged += (_, _) => OnClassChanged();

        var applyPresetBtn = new Button { Text = "Apply preset" };
        applyPresetBtn.Click += (_, _) => ApplyPreset();

        var initButton = new Button { Text = "Init as road" };
        initButton.Click += (_, _) => InitSelected();

        var applyButton = new Button { Text = "Apply attributes" };
        applyButton.Click += (_, _) => ApplySelected();

        var readButton = new Button { Text = "Read from selection" };
        readButton.Click += (_, _) => ReadSelection();

        var refreshButton = new Button { Text = "Refresh graph" };
        refreshButton.Click += (_, _) => RebuildGraph();

        var generateButton = new Button { Text = "Generate Road Surfaces" };
        generateButton.Click += (_, _) => GenerateSurfaces();

        var attrGroup = new GroupBox
        {
            Text = "Attributes",
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
                            new TableRow(new Label { Text = "preset" }, _presetDrop),
                            new TableRow(new Label { Text = "road_class" }, _classDropDown),
                            new TableRow(new Label { Text = "lanes" }, _lanesStepper),
                            new TableRow(new Label { Text = "width_m" }, _widthBox),
                            new TableRow(new Label { Text = "direction" }, _directionDrop),
                            new TableRow(new Label { Text = "corner_radius_m" }, _cornerRadiusBox),
                            new TableRow(new Label { Text = "median_width_m" }, _medianBox),
                            new TableRow(new Label { Text = "sidewalk_green_m" }, _sidewalkGreenBox),
                            new TableRow(new Label { Text = "parking_width_m" }, _parkingBox),
                        },
                    },
                    _terminalCheck,
                    new StackLayout
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 6,
                        Items = { applyPresetBtn, initButton, applyButton },
                    },
                    readButton,
                },
            },
        };

        var surfaceGroup = new GroupBox
        {
            Text = "Road surfaces",
            Content = new StackLayout
            {
                Padding = 6,
                Spacing = 4,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    generateButton,
                    _generateStatusLabel,
                    new Label
                    {
                        Text = "Hubs: external sidewalk fillets · crossings · arrows. Links: parking / greenery strips.",
                    },
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
                    new Label { Text = "Road Network" },
                    _totalLengthLabel,
                    _intersectionsLabel,
                    _deadEndsLabel,
                    _componentsLabel,
                    _byClassLabel,
                    new Label { Text = "Issues" },
                    _issuesText,
                    attrGroup,
                    surfaceGroup,
                    refreshButton,
                },
            },
        };
    }

    public void AttachServer(BridgeServer? server)
    {
        if (_server is not null)
            _server.RoadNetworkUpdated -= OnRoadNetworkUpdated;
        _server = server;
        if (_server is not null)
        {
            _server.RoadNetworkUpdated -= OnRoadNetworkUpdated;
            _server.RoadNetworkUpdated += OnRoadNetworkUpdated;
            if (_server.LatestRoadNetwork is { } graph)
                OnRoadNetworkUpdated(graph);
        }
    }

    public void DetachServer()
    {
        if (_server is not null)
            _server.RoadNetworkUpdated -= OnRoadNetworkUpdated;
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
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"[UrbanBridge] Rebuild error: {ex.Message}");
        }
    }

    public void PaintFromCache()
    {
        if (_server?.LatestRoadNetwork is { } graph)
            ApplyUi(graph);
    }

    public void RefreshSelectionLabel()
    {
        var doc = RhinoDoc.ActiveDoc;
        var n = doc is null ? 0 : RoadAttributeHelper.GetSelectedCurves(doc).Count;
        _selectionLabel.Text = $"Selected curves: {n}";
    }

    private void GenerateSurfaces()
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc is null)
        {
            _generateStatusLabel.Text = "No active document.";
            return;
        }

        try
        {
            var server = UrbanBridgePlugin.Instance?.Server;
            server?.RebuildAndSendRoadNetwork(doc);

            var graph = server?.LatestRoadNetwork;
            if (graph is null || graph.Edges.Count == 0)
            {
                _generateStatusLabel.Text = "No road edges.";
                return;
            }

            _generateStatusLabel.Text = "Generating…";
            var result = new RoadSurfaceGenerator(doc).Generate(doc, graph);

            if (server is not null)
            {
                server.MarkRoadSurfaceGenerated();
                server.NotifyRoadNetworkUpdated(graph);
                server.RebuildZoneAnalysis(doc);
            }
            else ApplyUi(graph);

            _generateStatusLabel.Text =
                $"Done: deleted {result.DeletedCount}, created {result.CreatedCount}" +
                (result.FailedLinks + result.FailedHubs > 0
                    ? $", failed L={result.FailedLinks} H={result.FailedHubs}" : "");
        }
        catch (Exception ex)
        {
            _generateStatusLabel.Text = "Error: " + ex.Message;
            RhinoApp.WriteLine($"[UrbanBridge] Generate failed: {ex.Message}");
        }
    }

    private void OnClassChanged()
    {
        var cls = SelectedClass();
        if (RoadAttributeHelper.ClassDefaults.TryGetValue(cls, out var d))
        {
            _lanesStepper.Value = d.Lanes;
            _widthBox.Text = Format(d.WidthM);
            _cornerRadiusBox.Text = Format(d.CornerRadiusM);
        }
    }

    private string SelectedClass() =>
        _classDropDown.SelectedIndex >= 0 && _classDropDown.SelectedIndex < RoadAttributeHelper.RoadClasses.Length
            ? RoadAttributeHelper.RoadClasses[_classDropDown.SelectedIndex] : "local";

    private string SelectedDirection() =>
        _directionDrop.SelectedIndex >= 0 && _directionDrop.SelectedIndex < RoadAttributeHelper.Directions.Length
            ? RoadAttributeHelper.Directions[_directionDrop.SelectedIndex] : "two_way";

    private string SelectedPreset() =>
        _presetDrop.SelectedIndex >= 0 && _presetDrop.SelectedIndex < RoadAttributeHelper.PresetNames.Length
            ? RoadAttributeHelper.PresetNames[_presetDrop.SelectedIndex] : "residential";

    private void ApplyPreset()
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

        // Ensure on Roads layer
        RoadAttributeHelper.InitAsRoad(doc, curves, SelectedClass());
        var name = SelectedPreset();
        var n = RoadAttributeHelper.ApplyPreset(doc, curves, name);
        RhinoApp.WriteLine($"[UrbanBridge] Preset '{name}' applied to {n} curve(s).");
        RebuildGraph();
        ReadSelection();
    }

    private void InitSelected()
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc is null) return;
        var curves = RoadAttributeHelper.GetSelectedCurves(doc);
        RefreshSelectionLabel();
        if (curves.Count == 0) return;
        var n = RoadAttributeHelper.InitAsRoad(doc, curves, SelectedClass());
        RhinoApp.WriteLine($"[UrbanBridge] Init as road: {n}");
        RebuildGraph();
        ReadSelection();
    }

    private void ApplySelected()
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc is null) return;
        var curves = RoadAttributeHelper.GetSelectedCurves(doc);
        RefreshSelectionLabel();
        if (curves.Count == 0) return;

        var cls = SelectedClass();
        var n = RoadAttributeHelper.ApplyAttributes(
            doc, curves, cls, (int)_lanesStepper.Value,
            ParseBox(_widthBox, RoadAttributeHelper.ClassDefaults[cls].WidthM),
            _terminalCheck.Checked == true,
            SelectedDirection(),
            ParseBox(_cornerRadiusBox, RoadAttributeHelper.ClassDefaults[cls].CornerRadiusM),
            ParseBox(_medianBox, 0),
            ParseBox(_sidewalkGreenBox, 0),
            ParseBox(_parkingBox, 0));
        RhinoApp.WriteLine($"[UrbanBridge] Applied attributes to {n}");
        RebuildGraph();
    }

    private void ReadSelection()
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc is null) return;
        var curves = RoadAttributeHelper.GetSelectedCurves(doc);
        RefreshSelectionLabel();
        var data = RoadAttributeHelper.ReadFirst(curves);
        if (data is null) return;

        var (cls, lanes, width, terminal, direction, radius, median, swGreen, parking) = data.Value;
        var idx = Array.FindIndex(RoadAttributeHelper.RoadClasses, c => c.Equals(cls, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0) _classDropDown.SelectedIndex = idx;
        _lanesStepper.Value = lanes;
        _widthBox.Text = Format(width);
        _terminalCheck.Checked = terminal;
        var dIdx = Array.FindIndex(RoadAttributeHelper.Directions, d => d.Equals(direction, StringComparison.OrdinalIgnoreCase));
        if (dIdx >= 0) _directionDrop.SelectedIndex = dIdx;
        _cornerRadiusBox.Text = Format(radius);
        _medianBox.Text = Format(median);
        _sidewalkGreenBox.Text = Format(swGreen);
        _parkingBox.Text = Format(parking);
    }

    private void OnRoadNetworkUpdated(RoadNetworkGraph graph)
    {
        _pendingGraph = graph;
        UiInvoke.Coalesce(ref _uiTimer, _uiGate, () =>
        {
            if (_pendingGraph is { } g) ApplyUi(g);
        });
    }

    private void ApplyUi(RoadNetworkGraph graph)
    {
        _totalLengthLabel.Text = $"Total length: {graph.Stats.TotalLengthM:F1} m";
        _intersectionsLabel.Text = $"Intersections: {graph.Stats.IntersectionCount}";
        _deadEndsLabel.Text = $"Dead ends: {graph.Stats.DeadEndCount}";
        _componentsLabel.Text = $"Components: {graph.Stats.ComponentCount}";

        _byClassLabel.Text = graph.Stats.LengthByClass.Count > 0
            ? "By class: " + string.Join(", ", graph.Stats.LengthByClass.OrderBy(kv => kv.Key)
                .Select(kv => $"{kv.Key}: {kv.Value:F1} m"))
            : "By class: —";

        if (graph.Issues.Count == 0)
        {
            _issuesText.Text = graph.Edges.Count == 0
                ? "No edges. Draw curves → Init / Apply preset."
                : "No issues.";
        }
        else
        {
            _issuesText.Text = UiInvoke.FormatCappedLines(
                graph.Issues.OrderByDescending(i => i.Severity).ThenBy(i => i.Type)
                    .Select(i => $"[{i.Severity}] {i.Type}: {i.Message}"));
        }

        RefreshSelectionLabel();
    }

    private static double ParseBox(TextBox box, double fallback) =>
        double.TryParse(box.Text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static string Format(double v) =>
        v.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
