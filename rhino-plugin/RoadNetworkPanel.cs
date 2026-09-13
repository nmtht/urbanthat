using Eto.Forms;
using Rhino;
using Rhino.UI;

namespace UrbanBridge.Rhino;

/// <summary>Rhino panel showing road-network statistics and validation issues.</summary>
/// <remarks>
/// Must stay a public type with GuidAttribute and a public parameterless constructor
/// (Rhino creates the instance when the panel is first opened).
/// </remarks>
[System.Runtime.InteropServices.Guid("A3F8C2E1-9B4D-4E7A-8F1C-2D5E6A9B0C3D")]
public class RoadNetworkPanel : Panel, IPanel
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
        Height = 200,
        Text = "No issues yet.\nAdd curves on layer Roads.",
    };
    private BridgeServer? _server;

    /// <summary>Same value as the GuidAttribute on this type.</summary>
    public static readonly System.Guid PanelId = new("A3F8C2E1-9B4D-4E7A-8F1C-2D5E6A9B0C3D");

    public RoadNetworkPanel()
    {
        // Keep construction minimal — exceptions here make OpenPanel appear to succeed with no UI.
        var refreshButton = new Button { Text = "Refresh / Обновить" };
        refreshButton.Click += (_, _) =>
        {
            try
            {
                if (RhinoDoc.ActiveDoc is { } doc && UrbanBridgePlugin.Instance?.Server is { } server)
                    server.RebuildAndSendRoadNetwork(doc);
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"[UrbanBridge] Panel refresh error: {ex.Message}");
            }
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
                    refreshButton,
                },
            },
        };

        RhinoApp.WriteLine("[UrbanBridge] RoadNetworkPanel instance created.");
    }

    /// <inheritdoc />
    public void PanelShown(uint documentSerialNumber, ShowPanelReason reason)
    {
        RhinoApp.WriteLine($"[UrbanBridge] PanelShown reason={reason} docSN={documentSerialNumber}");

        if (reason is not (ShowPanelReason.Show or ShowPanelReason.ShowOnDeactivate))
            return;

        _server = UrbanBridgePlugin.Instance?.Server;
        if (_server is null) return;

        _server.RoadNetworkUpdated -= OnRoadNetworkUpdated;
        _server.RoadNetworkUpdated += OnRoadNetworkUpdated;

        if (_server.LatestRoadNetwork is { } graph)
            OnRoadNetworkUpdated(graph);
        else if (RhinoDoc.ActiveDoc is { } doc)
            _server.RebuildAndSendRoadNetwork(doc);
    }

    /// <inheritdoc />
    public void PanelHidden(uint documentSerialNumber, ShowPanelReason reason)
    {
        RhinoApp.WriteLine($"[UrbanBridge] PanelHidden reason={reason}");
        if (reason == ShowPanelReason.HideOnDeactivate)
            return;

        if (_server is not null)
            _server.RoadNetworkUpdated -= OnRoadNetworkUpdated;
    }

    /// <inheritdoc />
    public void PanelClosing(uint documentSerialNumber, bool onCloseDocument)
    {
        RhinoApp.WriteLine($"[UrbanBridge] PanelClosing onCloseDocument={onCloseDocument}");
        if (_server is not null)
            _server.RoadNetworkUpdated -= OnRoadNetworkUpdated;
        _server = null;
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
                _issuesText.Text = "No issues.";
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
