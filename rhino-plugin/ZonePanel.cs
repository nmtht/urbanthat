using Eto.Forms;
using Rhino;
using Rhino.DocObjects;

namespace UrbanBridge.Rhino;

/// <summary>Floating Zone panel (list, metrics, issues, staleness warning).</summary>
public sealed class ZonePanelForm : Form
{
    private static ZonePanelForm? _instance;

    private readonly Label _summaryLabel = new() { Text = "Zones: —" };
    private readonly Label _staleLabel = new() { Text = "", TextColor = Eto.Drawing.Colors.DarkOrange };
    private readonly TextArea _zonesText = new() { ReadOnly = true, Wrap = true, Height = 160 };
    private readonly TextArea _issuesText = new() { ReadOnly = true, Wrap = true, Height = 140 };
    private readonly Button _refreshButton = new() { Text = "Refresh / Обновить" };

    private BridgeServer? _server;

    private ZonePanelForm()
    {
        Title = "UrbanBridge — Zones";
        ClientSize = new Eto.Drawing.Size(460, 520);
        MinimumSize = new Eto.Drawing.Size(340, 400);
        Padding = 10;

        _refreshButton.Click += (_, _) => Rebuild();

        Content = new StackLayout
        {
            Spacing = 6,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                new Label { Text = "Zoning" },
                _summaryLabel,
                _staleLabel,
                new Label { Text = "Zones (type · area · buildable · pop)" },
                _zonesText,
                new Label { Text = "Issues" },
                _issuesText,
                _refreshButton,
            },
        };

        Closed += (_, _) =>
        {
            if (_server is not null)
                _server.ZoneAnalysisUpdated -= OnUpdated;
            if (ReferenceEquals(_instance, this))
                _instance = null;
        };
    }

    public static void ShowOrFocus()
    {
        if (_instance is not null)
        {
            _instance.BringToFront();
            _instance.Rebuild();
            return;
        }

        var form = new ZonePanelForm();
        _instance = form;
        form.Owner = global::Rhino.UI.RhinoEtoApp.MainWindow;
        form._server = UrbanBridgePlugin.Instance?.Server;
        if (form._server is not null)
        {
            form._server.ZoneAnalysisUpdated -= form.OnUpdated;
            form._server.ZoneAnalysisUpdated += form.OnUpdated;
        }
        form.Rebuild();
        form.Show();
    }

    private void Rebuild()
    {
        try
        {
            if (RhinoDoc.ActiveDoc is { } doc && UrbanBridgePlugin.Instance?.Server is { } server)
                server.RebuildZoneAnalysis(doc);
            else if (_server?.LatestZoneAnalysis is { } a)
                OnUpdated(a);
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"[UrbanBridge] Zone rebuild error: {ex.Message}");
        }
    }

    private void OnUpdated(ZoneAnalysis analysis)
    {
        void Apply()
        {
            _summaryLabel.Text =
                $"Zones: {analysis.Zones.Count} · area {analysis.TotalAreaSqm:F0} m² · " +
                $"pop {analysis.TotalPopulation:F0} · jobs {analysis.TotalJobs:F0}";

            if (analysis.RoadSurfacesStale)
            {
                _staleLabel.Text =
                    "⚠ Road surfaces may be outdated — re-run Generate Road Surfaces after editing roads.";
            }
            else
            {
                _staleLabel.Text = analysis.LastRoadSurfaceGenUtc is null
                    ? "Road surfaces not generated yet (zone_no_road_access uses Stage 2.1 geometry)."
                    : "";
            }

            if (analysis.Zones.Count == 0)
            {
                _zonesText.Text = "No closed curves on layer Zones / Zones::*.";
            }
            else
            {
                var lines = analysis.Zones.Select(z =>
                {
                    analysis.MetricsById.TryGetValue(z.RhinoObjectId, out var m);
                    var area = m?.AreaSqm ?? 0;
                    var build = m?.BuildableAreaSqm ?? 0;
                    var pop = m?.EstimatedPopulation ?? 0;
                    var front = m?.RoadFrontageM ?? 0;
                    return $"{z.ZoneType,-12} {area,8:F0} m²  build {build,8:F0}  pop {pop,6:F0}  front {front,5:F0} m  [{z.RhinoObjectId.ToString()[..8]}]";
                });
                _zonesText.Text = string.Join("\n", lines);
            }

            if (analysis.Issues.Count == 0)
            {
                _issuesText.Text = "No issues.";
            }
            else
            {
                _issuesText.Text = string.Join("\n", analysis.Issues
                    .OrderByDescending(i => i.Severity)
                    .Select(i => $"[{i.Severity}] {i.Type}: {i.Message}"));
            }
        }

        try
        {
            if (Application.Instance != null)
                Application.Instance.AsyncInvoke(Apply);
            else
                Apply();
        }
        catch { Apply(); }
    }
}

/// <summary>Command: UrbanBridgeZones</summary>
[System.Runtime.InteropServices.Guid("B2C3D4E5-F6A7-4B8C-9D0E-1F2A3B4C5D6E")]
public sealed class UrbanBridgeZonesCommand : global::Rhino.Commands.Command
{
    public override string EnglishName => "UrbanBridgeZones";

    protected override global::Rhino.Commands.Result RunCommand(RhinoDoc doc, global::Rhino.Commands.RunMode mode)
    {
        ZonePanelForm.ShowOrFocus();
        return global::Rhino.Commands.Result.Success;
    }
}
