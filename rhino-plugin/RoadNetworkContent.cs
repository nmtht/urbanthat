using Eto.Forms;
using Rhino;

namespace UrbanBridge.Plugin;

/// <summary>Roads tab: graph stats, issues, Init/Apply road attributes, Generate surfaces.</summary>
public sealed class RoadNetworkContent : Panel
{
    private readonly Label _summaryLabel = new() { Text = "Road network: —" };
    private readonly TextArea _issuesText = new() { ReadOnly = true, Wrap = true, Height = 100 };
    private readonly Label _selectionLabel = new() { Text = "Selected curves: 0" };
    private readonly DropDown _classDrop = new();
    private readonly TextBox _lanesBox = new() { Text = "2", Width = 60 };
    private readonly TextBox _widthBox = new() { Text = "8", Width = 60 };
    private readonly DropDown _dirDrop = new();
    private readonly TextBox _radiusBox = new() { Text = "4", Width = 60 };
    private readonly DropDown _presetDrop = new();

    private BridgeServer? _server;
    private readonly object _uiGate = new();
    private System.Threading.Timer? _uiTimer;
    private RoadNetworkGraph? _pending;

    public RoadNetworkContent()
    {
        foreach (var c in RoadAttributeHelper.RoadClasses)
            _classDrop.Items.Add(c);
        _classDrop.SelectedIndex = 2; // local

        foreach (var d in RoadAttributeHelper.Directions)
            _dirDrop.Items.Add(d);
        _dirDrop.SelectedIndex = 0;

        foreach (var p in RoadAttributeHelper.PresetNames)
            _presetDrop.Items.Add(p);

        var initBtn = new Button { Text = "Init as road" };
        initBtn.Click += (_, _) => InitSelected();
        var applyBtn = new Button { Text = "Apply attributes" };
        applyBtn.Click += (_, _) => ApplySelected();
        var presetBtn = new Button { Text = "Apply preset" };
        presetBtn.Click += (_, _) => ApplyPreset();
        var genBtn = new Button { Text = "Generate Road Surfaces" };
        genBtn.Click += (_, _) => GenerateSurfaces();
        var refreshBtn = new Button { Text = "Refresh graph" };
        refreshBtn.Click += (_, _) => RebuildGraph();

        var attrGroup = new GroupBox
        {
            Text = "Attributes (selected curves)",
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
                            new TableRow(new Label { Text = "road_class" }, _classDrop),
                            new TableRow(new Label { Text = "lanes" }, _lanesBox),
                            new TableRow(new Label { Text = "width_m" }, _widthBox),
                            new TableRow(new Label { Text = "direction" }, _dirDrop),
                            new TableRow(new Label { Text = "corner_radius_m" }, _radiusBox),
                            new TableRow(new Label { Text = "preset" }, _presetDrop),
                        },
                    },
                    new StackLayout
                    {
                        Orientation = Orientation.Horizontal, Spacing = 6,
                        Items = { initBtn, applyBtn, presetBtn },
                    },
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
                    _summaryLabel,
                    new Label { Text = "Issues" },
                    _issuesText,
                    attrGroup,
                    genBtn,
                    refreshBtn,
                },
            },
        };
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
        var cls = SelectedClass();
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
        var cls = SelectedClass();
        var lanes = int.TryParse(_lanesBox.Text, out var l) ? l : 2;
        var width = double.TryParse(_widthBox.Text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var w) ? w : 8;
        var dir = _dirDrop.SelectedIndex == 1 ? "one_way" : "two_way";
        var radius = double.TryParse(_radiusBox.Text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var r) ? r : 4;
        var n = RoadAttributeHelper.ApplyAttributes(
            doc, curves, cls, lanes, width, false, dir, radius, 0, 0, 0);
        RhinoApp.WriteLine($"[UrbanBridge] Applied road attrs to {n}");
        RebuildGraph();
    }

    private void ApplyPreset()
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc is null) return;
        var curves = RoadAttributeHelper.GetSelectedCurves(doc);
        if (curves.Count == 0 || _presetDrop.SelectedIndex < 0) return;
        var name = RoadAttributeHelper.PresetNames[_presetDrop.SelectedIndex];
        var n = RoadAttributeHelper.ApplyPreset(doc, curves, name);
        RhinoApp.WriteLine($"[UrbanBridge] Applied preset {name} to {n}");
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

    private string SelectedClass()
    {
        if (_classDrop.SelectedIndex >= 0 && _classDrop.SelectedIndex < RoadAttributeHelper.RoadClasses.Length)
            return RoadAttributeHelper.RoadClasses[_classDrop.SelectedIndex];
        return "local";
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
