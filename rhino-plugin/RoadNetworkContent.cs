using Eto.Forms;
using Rhino;

namespace UrbanBridge.Rhino;

/// <summary>
/// Shared UI: stats, issues, attribute editor, and Generate Road Surfaces.
/// </summary>
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
        Height = 140,
        Text = "No issues yet.\nAdd curves on layer Roads.",
    };

    private readonly Label _selectionLabel = new() { Text = "Selected curves: 0" };
    private readonly DropDown _classDropDown = new();
    private readonly NumericStepper _lanesStepper = new()
    {
        MinValue = 0,
        MaxValue = 16,
        DecimalPlaces = 0,
        Value = 2,
        Width = 80,
    };
    private readonly TextBox _widthBox = new() { Text = "8", Width = 80 };
    private readonly CheckBox _terminalCheck = new() { Text = "is_terminal (intentional dead-end)", Checked = false };
    private readonly Label _generateStatusLabel = new() { Text = "" };

    private BridgeServer? _server;

    public RoadNetworkContent()
    {
        foreach (var c in RoadAttributeHelper.RoadClasses)
            _classDropDown.Items.Add(c);
        _classDropDown.SelectedIndex = 2; // local

        _classDropDown.SelectedIndexChanged += (_, _) => OnClassChanged();

        var initButton = new Button { Text = "Init as road (UserText + layer Roads)" };
        initButton.Click += (_, _) => InitSelected();

        var applyButton = new Button { Text = "Apply attributes to selection" };
        applyButton.Click += (_, _) => ApplySelected();

        var readButton = new Button { Text = "Read from selection" };
        readButton.Click += (_, _) => ReadSelection();

        var refreshButton = new Button { Text = "Refresh graph / Обновить" };
        refreshButton.Click += (_, _) => RebuildGraph();

        var generateButton = new Button { Text = "Generate Road Surfaces" };
        generateButton.Click += (_, _) => GenerateSurfaces();

        var attrGroup = new GroupBox
        {
            Text = "Attributes (selected curves)",
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
                            new TableRow(new Label { Text = "road_class" }, _classDropDown),
                            new TableRow(new Label { Text = "lanes" }, _lanesStepper),
                            new TableRow(new Label { Text = "width_m" }, _widthBox),
                        },
                    },
                    _terminalCheck,
                    new StackLayout
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 6,
                        Items = { initButton, applyButton },
                    },
                    readButton,
                },
            },
        };

        var surfaceGroup = new GroupBox
        {
            Text = "Road surfaces (Stage 2.1)",
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
                        Text = "Manual only — deletes previous generated objects, builds roadway / sidewalk / lane lines.",
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
    }

    public void RebuildGraph()
    {
        try
        {
            if (RhinoDoc.ActiveDoc is { } doc && UrbanBridgePlugin.Instance?.Server is { } server)
                server.RebuildAndSendRoadNetwork(doc);
            else if (_server?.LatestRoadNetwork is { } graph)
                OnRoadNetworkUpdated(graph);
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"[UrbanBridge] Rebuild error: {ex.Message}");
        }
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
            // Ensure graph is current
            var server = UrbanBridgePlugin.Instance?.Server;
            if (server is not null)
                server.RebuildAndSendRoadNetwork(doc);

            var graph = server?.LatestRoadNetwork;
            if (graph is null || graph.Edges.Count == 0)
            {
                _generateStatusLabel.Text = "No road edges — add curves on Roads first.";
                RhinoApp.WriteLine("[UrbanBridge] Generate Road Surfaces: empty graph.");
                return;
            }

            _generateStatusLabel.Text = "Generating…";
            var generator = new RoadSurfaceGenerator(doc);
            var result = generator.Generate(doc, graph);

            // Push updated issues (acute / generation_failed) to panel + clients
            if (server is not null)
            {
                // Re-broadcast graph with extra issues without full rebuild of topology
                server.NotifyRoadNetworkUpdated(graph);
            }
            else
            {
                OnRoadNetworkUpdated(graph);
            }

            _generateStatusLabel.Text =
                $"Done: deleted {result.DeletedCount}, created {result.CreatedCount}" +
                (result.FailedLinks + result.FailedHubs > 0
                    ? $", failed links={result.FailedLinks} hubs={result.FailedHubs}"
                    : "");

            RhinoApp.WriteLine(
                $"[UrbanBridge] Road surfaces: deleted={result.DeletedCount}, created={result.CreatedCount}, " +
                $"failed links={result.FailedLinks}, hubs={result.FailedHubs}");
        }
        catch (Exception ex)
        {
            _generateStatusLabel.Text = "Error: " + ex.Message;
            RhinoApp.WriteLine($"[UrbanBridge] Generate Road Surfaces failed: {ex.Message}");
        }
    }

    private void OnClassChanged()
    {
        var cls = SelectedClass();
        if (RoadAttributeHelper.ClassDefaults.TryGetValue(cls, out var d))
        {
            _lanesStepper.Value = d.Lanes;
            _widthBox.Text = d.WidthM.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private string SelectedClass()
    {
        if (_classDropDown.SelectedIndex >= 0 && _classDropDown.SelectedIndex < RoadAttributeHelper.RoadClasses.Length)
            return RoadAttributeHelper.RoadClasses[_classDropDown.SelectedIndex];
        return "local";
    }

    private void InitSelected()
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc is null) return;
        var curves = RoadAttributeHelper.GetSelectedCurves(doc);
        RefreshSelectionLabel();
        if (curves.Count == 0)
        {
            RhinoApp.WriteLine("[UrbanBridge] Select one or more curves first.");
            return;
        }

        var n = RoadAttributeHelper.InitAsRoad(doc, curves, SelectedClass());
        RhinoApp.WriteLine($"[UrbanBridge] Init as road: {n} curve(s) → layer Roads + default UserText.");
        RebuildGraph();
        ReadSelection();
    }

    private void ApplySelected()
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc is null) return;
        var curves = RoadAttributeHelper.GetSelectedCurves(doc);
        RefreshSelectionLabel();
        if (curves.Count == 0)
        {
            RhinoApp.WriteLine("[UrbanBridge] Select one or more curves first.");
            return;
        }

        var cls = SelectedClass();
        var lanes = (int)_lanesStepper.Value;
        if (!double.TryParse(
                _widthBox.Text,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var width))
        {
            width = RoadAttributeHelper.ClassDefaults[cls].WidthM;
        }

        var terminal = _terminalCheck.Checked == true;
        var n = RoadAttributeHelper.ApplyAttributes(doc, curves, cls, lanes, width, terminal);
        RhinoApp.WriteLine($"[UrbanBridge] Applied attributes to {n} curve(s): class={cls}, lanes={lanes}, width={width}, terminal={terminal}.");
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

        var (cls, lanes, width, terminal) = data.Value;
        var idx = Array.FindIndex(RoadAttributeHelper.RoadClasses, c =>
            c.Equals(cls, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
            _classDropDown.SelectedIndex = idx;
        _lanesStepper.Value = lanes;
        _widthBox.Text = width.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _terminalCheck.Checked = terminal;
    }

    private void OnRoadNetworkUpdated(RoadNetworkGraph graph)
    {
        void Apply()
        {
            _totalLengthLabel.Text = $"Total length: {graph.Stats.TotalLengthM:F1} m";
            _intersectionsLabel.Text = $"Intersections: {graph.Stats.IntersectionCount}";
            _deadEndsLabel.Text = $"Dead ends: {graph.Stats.DeadEndCount}";
            _componentsLabel.Text = $"Components: {graph.Stats.ComponentCount}";

            if (graph.Stats.LengthByClass.Count > 0)
            {
                var parts = graph.Stats.LengthByClass
                    .OrderBy(kv => kv.Key)
                    .Select(kv => $"{kv.Key}: {kv.Value:F1} m");
                _byClassLabel.Text = "By class: " + string.Join(", ", parts);
            }
            else
            {
                _byClassLabel.Text = "By class: —";
            }

            if (graph.Issues.Count == 0)
            {
                _issuesText.Text = graph.Edges.Count == 0
                    ? "No edges.\nCreate layer 'Roads', draw curves, then Init as road."
                    : "No issues.";
            }
            else
            {
                var lines = graph.Issues
                    .OrderByDescending(i => i.Severity)
                    .ThenBy(i => i.Type)
                    .Select(i => $"[{i.Severity}] {i.Type}: {i.Message}");
                _issuesText.Text = string.Join("\n", lines);
            }

            RefreshSelectionLabel();
        }

        try
        {
            if (Application.Instance != null)
                Application.Instance.AsyncInvoke(Apply);
            else
                Apply();
        }
        catch
        {
            Apply();
        }
    }
}
