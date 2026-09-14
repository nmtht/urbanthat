using Eto.Forms;
using Rhino;

namespace UrbanBridge.Rhino;

/// <summary>
/// Modeless Eto window — works even when PlugIn.Id is Empty and dockable panels cannot register.
/// </summary>
public sealed class RoadNetworkForm : Form
{
    private static RoadNetworkForm? _openInstance;

    private readonly Label _totalLengthLabel = new() { Text = "Total length: —" };
    private readonly Label _intersectionsLabel = new() { Text = "Intersections: —" };
    private readonly Label _deadEndsLabel = new() { Text = "Dead ends: —" };
    private readonly Label _componentsLabel = new() { Text = "Components: —" };
    private readonly Label _byClassLabel = new() { Text = "By class: —" };
    private readonly TextArea _issuesText = new()
    {
        ReadOnly = true,
        Wrap = true,
        Height = 220,
        Text = "No issues yet.\nAdd curves on layer Roads.",
    };
    private BridgeServer? _server;

    private RoadNetworkForm()
    {
        Title = "UrbanBridge — Road Network";
        ClientSize = new Eto.Drawing.Size(420, 480);
        MinimumSize = new Eto.Drawing.Size(320, 360);
        Padding = 10;

        var refreshButton = new Button { Text = "Refresh / Обновить" };
        refreshButton.Click += (_, _) => Rebuild();

        Content = new StackLayout
        {
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
                refreshButton,
            },
        };

        Closed += (_, _) =>
        {
            if (_server is not null)
                _server.RoadNetworkUpdated -= OnRoadNetworkUpdated;
            if (ReferenceEquals(_openInstance, this))
                _openInstance = null;
        };
    }

    public static void ShowOrFocus()
    {
        if (_openInstance is not null)
        {
            _openInstance.BringToFront();
            _openInstance.Rebuild();
            return;
        }

        var form = new RoadNetworkForm();
        _openInstance = form;
        form.Owner = Rhino.UI.RhinoEtoApp.MainWindow;
        form.SubscribeAndRebuild();
        form.Show();
    }

    private void SubscribeAndRebuild()
    {
        _server = UrbanBridgePlugin.Instance?.Server;
        if (_server is not null)
        {
            _server.RoadNetworkUpdated -= OnRoadNetworkUpdated;
            _server.RoadNetworkUpdated += OnRoadNetworkUpdated;
        }
        Rebuild();
    }

    private void Rebuild()
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
            RhinoApp.WriteLine($"[UrbanBridge] RoadNetworkForm rebuild error: {ex.Message}");
        }
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
                    ? "No edges.\nCreate layer 'Roads' and add curves."
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
