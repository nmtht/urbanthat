using Eto.Drawing;
using Eto.Forms;
using Rhino;

namespace UrbanBridge.Plugin;

/// <summary>Zoning tab — forced dark theme, white text.</summary>
public sealed class ZoneTabContent : Panel
{
    private readonly Label _summaryLabel = new() { Text = "Zones: —", TextColor = Colors.White };
    private readonly Label _staleLabel = new() { Text = "", TextColor = UiTheme.Danger };
    private readonly TextArea _zonesText = new()
    {
        ReadOnly = true, Wrap = true, Height = 90,
        TextColor = Colors.White, BackgroundColor = UiTheme.InputBg,
    };
    private readonly TextArea _issuesText = new()
    {
        ReadOnly = true, Wrap = true, Height = 70,
        TextColor = Colors.White, BackgroundColor = UiTheme.InputBg,
    };
    private readonly Label _selectionLabel = new() { Text = "Selected curves: 0", TextColor = UiTheme.Muted };

    private readonly UiPresetBar _typePresets;
    private readonly UiPresetBar _massingPresets;
    private readonly UiValueSlider _farSlider;
    private readonly UiValueSlider _greenSlider;
    private readonly UiValueSlider _heightSlider;
    private readonly UiValueSlider _setbackSlider;

    private BridgeServer? _server;
    private readonly object _uiGate = new();
    private System.Threading.Timer? _uiTimer;
    private ZoneAnalysis? _pending;
    private bool _syncingUi;

    public ZoneTabContent()
    {
        BackgroundColor = UiTheme.PanelBg;

        _typePresets = new UiPresetBar(ZoneTypeDefaults.ZoneTypes, 0);
        _typePresets.SelectedIndexChanged += (_, _) =>
        {
            if (_syncingUi) return;
            OnTypeChanged();
        };

        _massingPresets = new UiPresetBar(MassingGenerator.MassingTypes, 0);

        _farSlider = new UiValueSlider("FAR", 0.2, 6.0, 1.5) { Step = 0.05, FormatString = "0.00" };
        _greenSlider = new UiValueSlider("Green ratio", 0, 0.9, 0.25) { Step = 0.05, FormatString = "0.00" };
        _heightSlider = new UiValueSlider("Height max", 6, 80, 24) { Step = 1, Unit = " m", FormatString = "0" };
        _setbackSlider = new UiValueSlider("Setback", 0, 20, 3) { Step = 0.5, Unit = " m", FormatString = "0.#" };

        var initBtn = new Button { Text = "Init as zone", TextColor = Colors.White };
        initBtn.Click += (_, _) => InitSelected();
        var applyBtn = new Button { Text = "Apply attributes", TextColor = Colors.White };
        applyBtn.Click += (_, _) => ApplySelected();
        var readBtn = new Button { Text = "Read selection", TextColor = Colors.White };
        readBtn.Click += (_, _) => ReadSelection();
        var refreshBtn = new Button { Text = "Refresh", TextColor = Colors.White };
        refreshBtn.Click += (_, _) => Rebuild();
        var proxyBtn = new Button { Text = "Regenerate proxies", TextColor = Colors.White };
        proxyBtn.Click += (_, _) => RegenerateProxies();
        var massingBtn = new Button { Text = "Generate massing + courtyards", TextColor = Colors.White };
        massingBtn.Click += (_, _) => GenerateMassing();

        var attrLayout = new DynamicLayout { Padding = 8, Spacing = new Size(6, 6), BackgroundColor = UiTheme.CardBg };
        attrLayout.AddRow(_selectionLabel);
        attrLayout.AddRow(new Label { Text = "zone_type", TextColor = Colors.White, Font = Fonts.Sans(8) });
        attrLayout.AddRow(_typePresets);
        attrLayout.AddRow(new Label { Text = "massing_type", TextColor = Colors.White, Font = Fonts.Sans(8) });
        attrLayout.AddRow(_massingPresets);
        attrLayout.AddRow(_farSlider);
        attrLayout.AddRow(_greenSlider);
        attrLayout.AddRow(_heightSlider);
        attrLayout.AddRow(_setbackSlider);
        attrLayout.AddRow(new TableLayout
        {
            Spacing = new Size(6, 0),
            Rows = { new TableRow(initBtn, applyBtn, readBtn, null) },
        });

        var attrGroup = new GroupBox
        {
            Text = "Attributes (selected closed curves)",
            TextColor = Colors.White,
            BackgroundColor = UiTheme.CardBg,
            Content = attrLayout,
        };

        var genLayout = new DynamicLayout { Padding = 8, Spacing = new Size(4, 4), BackgroundColor = UiTheme.CardBg };
        genLayout.AddRow(proxyBtn);
        genLayout.AddRow(massingBtn);
        genLayout.AddRow(new Label
        {
            Text = "Presets + sliders write UserText. Apply then Generate.\n" +
                   "Courtyards scale with green_ratio. Facades → Architecture.",
            TextColor = UiTheme.Muted,
        });

        var genGroup = new GroupBox
        {
            Text = "Generate",
            TextColor = Colors.White,
            BackgroundColor = UiTheme.CardBg,
            Content = genLayout,
        };

        var root = new DynamicLayout { Padding = 12, Spacing = new Size(8, 6), BackgroundColor = UiTheme.PanelBg };
        root.AddRow(new Label
        {
            Text = "Zoning",
            Font = new Font(SystemFont.Bold, 13),
            TextColor = Colors.White,
        });
        root.AddRow(_summaryLabel);
        root.AddRow(_staleLabel);
        root.AddRow(new Label
        {
            Text = "Zones",
            Font = new Font(SystemFont.Bold, 10),
            TextColor = Colors.White,
        });
        root.AddRow(_zonesText);
        root.AddRow(new Label
        {
            Text = "Issues",
            Font = new Font(SystemFont.Bold, 10),
            TextColor = Colors.White,
        });
        root.AddRow(_issuesText);
        root.AddRow(attrGroup);
        root.AddRow(genGroup);
        root.AddRow(refreshBtn);

        Content = new Scrollable
        {
            Border = BorderType.None,
            BackgroundColor = UiTheme.PanelBg,
            Content = root,
        };

        UiTheme.ApplyDark(this);
        OnTypeChanged();
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
        var type = _typePresets.SelectedItem;
        if (string.IsNullOrEmpty(type)) type = "residential";
        var d = ZoneTypeDefaults.Get(type);
        _syncingUi = true;
        try
        {
            _farSlider.Value = d.Far;
            _heightSlider.Value = d.HeightMaxM;
            _setbackSlider.Value = ZoneTypeDefaults.DefaultSetbackM;
            _greenSlider.Value = d.GreenRatio;
        }
        finally { _syncingUi = false; }
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
        var n = ZoneAttributeHelper.InitAsZone(
            doc, curves, _typePresets.SelectedItem, _massingPresets.SelectedItem);
        RhinoApp.WriteLine($"[UrbanBridge] Init as zone: {n} curve(s).");
        Rebuild();
        RegenerateProxies();
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
        var type = _typePresets.SelectedItem;
        if (string.IsNullOrEmpty(type)) type = "residential";
        var n = ZoneAttributeHelper.ApplyAttributes(
            doc, curves, type,
            _farSlider.Value, _heightSlider.Value, _setbackSlider.Value, _greenSlider.Value,
            _massingPresets.SelectedItem);
        RhinoApp.WriteLine($"[UrbanBridge] Applied zone attrs to {n}");
        Rebuild();
        RegenerateProxies();
    }

    private void ReadSelection()
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc is null) return;
        var curves = ZoneAttributeHelper.GetSelectedCurves(doc);
        RefreshSelectionLabel();
        var data = ZoneAttributeHelper.ReadFirst(curves);
        if (data is null) return;
        var (type, far, height, setback, green, massing) = data.Value;
        _syncingUi = true;
        try
        {
            _typePresets.SelectByName(type);
            _massingPresets.SelectByName(massing);
            _farSlider.Value = far;
            _heightSlider.Value = height;
            _setbackSlider.Value = setback;
            _greenSlider.Value = green;
        }
        finally { _syncingUi = false; }
    }

    private void RegenerateProxies()
    {
        try
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc is null) return;
            var server = UrbanBridgePlugin.Instance?.Server;
            if (server is null) return;
            server.RebuildZoneAnalysis(doc);
            var analysis = server.LatestZoneAnalysis;
            if (analysis is null || analysis.Zones.Count == 0)
            {
                RhinoApp.WriteLine("[UrbanBridge] No zones for proxies.");
                return;
            }
            var n = new ZoneProxyGenerator(doc).RegenerateAll(doc, analysis);
            RhinoApp.WriteLine($"[UrbanBridge] Zone proxies: {n} piece(s).");
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"[UrbanBridge] Proxy regen failed: {ex.Message}");
        }
    }

    private void GenerateMassing()
    {
        try
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc is null) return;
            var server = UrbanBridgePlugin.Instance?.Server;
            if (server is null)
            {
                RhinoApp.WriteLine("[UrbanBridge] Plugin server not running.");
                return;
            }
            server.RebuildZoneAnalysis(doc);
            var analysis = server.LatestZoneAnalysis;
            if (analysis is null || analysis.Zones.Count == 0)
            {
                RhinoApp.WriteLine("[UrbanBridge] No zones. Init closed curves as zones first.");
                return;
            }
            var batch = new MassingGenerator(doc).Generate(doc, analysis);
            server.LatestMassingBuiltGfaSqm = batch.TotalBuiltFloorAreaSqm;
            RhinoApp.WriteLine(
                $"[UrbanBridge] Massing: created {batch.CreatedCount}, deleted {batch.DeletedCount}, " +
                $"courtyard {batch.CourtyardCount}, GFA {batch.TotalBuiltFloorAreaSqm:F0} m²");
            if (PluginSettings.GenerateFacadesWithMassing)
            {
                var fn = new FacadeGenerator(doc).GenerateFromMassing(doc, PluginSettings.GreenRoof);
                RhinoApp.WriteLine($"[UrbanBridge] Facades/roof: {fn}");
            }
            if (PluginSettings.GenerateTreesForGreenZones)
            {
                var tn = new TreeGenerator(doc).GenerateForGreenZones(doc, analysis);
                RhinoApp.WriteLine($"[UrbanBridge] Trees: {tn}");
            }
            PaintFromCache();
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"[UrbanBridge] Massing failed: {ex.Message}");
        }
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
        _summaryLabel.TextColor = Colors.White;
        if (analysis.RoadSurfacesStale)
            _staleLabel.Text = "⚠ Road surfaces may be outdated — re-run Generate Road Surfaces.";
        else if (analysis.LastRoadSurfaceGenUtc is null)
            _staleLabel.Text = "Road surfaces not generated yet (needed for proxy clip / road access).";
        else
            _staleLabel.Text = "";
        if (analysis.Zones.Count == 0)
            _zonesText.Text = "No closed curves on Zones. Select curves → Init as zone.";
        else
        {
            var lines = analysis.Zones.Select(z =>
            {
                analysis.MetricsById.TryGetValue(z.RhinoObjectId, out var m);
                return $"{z.ZoneType,-12} {z.MassingType,-10} {(m?.AreaSqm ?? 0),8:F0} m²  FAR {z.Far:F1}";
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
}
