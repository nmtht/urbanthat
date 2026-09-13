using System.ComponentModel;
using Eto.Drawing;
using Eto.Forms;
using Rhino;
using Rhino.UI;

namespace UrbanBridge.Rhino;

/// <summary>Rhino panel showing road-network statistics and validation issues.</summary>
[System.Runtime.InteropServices.Guid("A3F8C2E1-9B4D-4E7A-8F1C-2D5E6A9B0C3D")]
public sealed class RoadNetworkPanel : Panel, IPanel
{
    private readonly Label _totalLengthLabel = new() { Text = "Total length: —" };
    private readonly Label _intersectionsLabel = new() { Text = "Intersections: —" };
    private readonly Label _deadEndsLabel = new() { Text = "Dead ends: —" };
    private readonly Label _componentsLabel = new() { Text = "Components: —" };
    private readonly Label _byClassLabel = new() { Text = "By class: —" };
    private readonly GridView _issuesGrid;
    private readonly List<IssueRow> _issueRows = new();
    private BridgeServer? _server;

    public static readonly System.Guid PanelId = new("A3F8C2E1-9B4D-4E7A-8F1C-2D5E6A9B0C3D");

    public RoadNetworkPanel()
    {
        _issuesGrid = new GridView
        {
            DataStore = _issueRows,
            Height = 220,
        };
        _issuesGrid.Columns.Add(new GridColumn
        {
            HeaderText = "Severity",
            DataCell = new TextBoxCell { Binding = Binding.Property<IssueRow, string>(r => r.Severity) },
            Width = 70,
        });
        _issuesGrid.Columns.Add(new GridColumn
        {
            HeaderText = "Type",
            DataCell = new TextBoxCell { Binding = Binding.Property<IssueRow, string>(r => r.Type) },
            Width = 140,
        });
        _issuesGrid.Columns.Add(new GridColumn
        {
            HeaderText = "Message",
            DataCell = new TextBoxCell { Binding = Binding.Property<IssueRow, string>(r => r.Message) },
            Width = 280,
        });

        _issuesGrid.SelectedItemsChanged += OnIssueSelected;

        var refreshButton = new Button { Text = "Обновить" };
        refreshButton.Click += (_, _) =>
        {
            if (RhinoDoc.ActiveDoc is { } doc && UrbanBridgePlugin.Instance.Server is { } server)
                server.RebuildAndSendRoadNetwork(doc);
        };

        Content = new StackLayout
        {
            Padding = 8,
            Spacing = 6,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                new Label { Text = "Road Network", Font = SystemFonts.Bold() },
                _totalLengthLabel,
                _intersectionsLabel,
                _deadEndsLabel,
                _componentsLabel,
                _byClassLabel,
                new Label { Text = "Issues", Font = SystemFonts.Bold() },
                new Scrollable { Content = _issuesGrid, ExpandContentWidth = true, ExpandContentHeight = true },
                refreshButton,
            },
        };
    }

    public void PanelShown(uint documentSerialNumber, bool on)
    {
        if (!on) return;
        _server = UrbanBridgePlugin.Instance.Server;
        if (_server is null) return;

        _server.RoadNetworkUpdated -= OnRoadNetworkUpdated;
        _server.RoadNetworkUpdated += OnRoadNetworkUpdated;

        if (_server.LatestRoadNetwork is { } graph)
            OnRoadNetworkUpdated(graph);
        else if (RhinoDoc.ActiveDoc is { } doc)
            _server.RebuildAndSendRoadNetwork(doc);
    }

    public void PanelHidden(uint documentSerialNumber, bool on) { }

    public void PanelClosing(uint documentSerialNumber, bool on)
    {
        if (_server is not null)
            _server.RoadNetworkUpdated -= OnRoadNetworkUpdated;
    }

    private void OnRoadNetworkUpdated(RoadNetworkGraph graph)
    {
        Application.Instance.AsyncInvoke(() =>
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

            _issueRows.Clear();
            foreach (var issue in graph.Issues
                         .OrderByDescending(i => i.Severity)
                         .ThenBy(i => i.Type))
            {
                _issueRows.Add(new IssueRow
                {
                    Severity = issue.Severity.ToString(),
                    Type = issue.Type,
                    Message = issue.Message,
                    RelatedEdgeIds = issue.RelatedEdgeIds.ToList(),
                    RelatedNodeId = issue.RelatedNodeId,
                });
            }
            _issuesGrid.DataStore = null;
            _issuesGrid.DataStore = _issueRows;
        });
    }

    private void OnIssueSelected(object? sender, EventArgs e)
    {
        if (_issuesGrid.SelectedItem is not IssueRow row) return;
        if (RhinoDoc.ActiveDoc is not { } doc) return;

        doc.Objects.UnselectAll();
        var bbox = Rhino.Geometry.BoundingBox.Empty;

        foreach (var edgeId in row.RelatedEdgeIds)
        {
            var obj = doc.Objects.FindId(edgeId);
            if (obj is null) continue;
            obj.Select(true);
            var objBbox = obj.Geometry.GetBoundingBox(true);
            if (bbox.IsValid) bbox.Union(objBbox);
            else bbox = objBbox;
        }

        if (bbox.IsValid)
        {
            var views = doc.Views;
            foreach (var view in views)
            {
                view.ActiveViewport.ZoomBoundingBox(bbox);
                view.Redraw();
            }
        }
        else
        {
            doc.Views.Redraw();
        }
    }

    private sealed class IssueRow
    {
        public string Severity { get; set; } = "";
        public string Type { get; set; } = "";
        public string Message { get; set; } = "";
        public List<Guid> RelatedEdgeIds { get; set; } = new();
        public string? RelatedNodeId { get; set; }
    }
}
