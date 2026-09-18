using Eto.Drawing;
using Eto.Forms;

namespace UrbanBridge.Plugin;

/// <summary>
/// Tabs: Dashboard | Roads | Zoning | Architecture.
/// Forced dark theme independent of Rhino appearance.
/// </summary>
public sealed class UrbanBridgeMainContent : Panel
{
    private readonly DashboardTabContent _dashboard = new();
    private readonly RoadNetworkContent _roads = new();
    private readonly ZoneTabContent _zones = new();
    private readonly ArchitectureTabContent _architecture = new();
    private readonly TabControl _tabs;

    public UrbanBridgeMainContent()
    {
        BackgroundColor = UiTheme.PanelBg;

        _tabs = new TabControl { BackgroundColor = UiTheme.PanelBg };
        _tabs.Pages.Add(new TabPage { Text = "Dashboard", Content = _dashboard, BackgroundColor = UiTheme.PanelBg });
        _tabs.Pages.Add(new TabPage { Text = "Roads", Content = _roads, BackgroundColor = UiTheme.PanelBg });
        _tabs.Pages.Add(new TabPage { Text = "Zoning", Content = _zones, BackgroundColor = UiTheme.PanelBg });
        _tabs.Pages.Add(new TabPage { Text = "Architecture", Content = _architecture, BackgroundColor = UiTheme.PanelBg });
        _tabs.SelectedIndexChanged += (_, _) => RefreshActiveTab(fromCacheOnly: true);
        Content = _tabs;

        // Re-apply after layout in case platform overrides colors
        LoadComplete += (_, _) => UiTheme.ApplyDark(this);
        UiTheme.ApplyDark(this);
    }

    public int SelectedTabIndex => _tabs.SelectedIndex;

    public void AttachServer(BridgeServer? server)
    {
        _dashboard.AttachServer(server);
        _roads.AttachServer(server);
        _zones.AttachServer(server);
        _architecture.AttachServer(server);
    }

    public void DetachServer()
    {
        _dashboard.DetachServer();
        _roads.DetachServer();
        _zones.DetachServer();
        _architecture.DetachServer();
    }

    public void RefreshActiveTab(bool fromCacheOnly = false)
    {
        switch (_tabs.SelectedIndex)
        {
            case 0:
                _dashboard.PaintFromCache();
                break;
            case 1:
                if (!fromCacheOnly || UrbanBridgePlugin.Instance?.Server?.LatestRoadNetwork is null)
                    _roads.RebuildGraph();
                else
                    _roads.PaintFromCache();
                _roads.RefreshSelectionLabel();
                break;
            case 2:
                if (!fromCacheOnly || UrbanBridgePlugin.Instance?.Server?.LatestZoneAnalysis is null)
                    _zones.Rebuild();
                else
                    _zones.PaintFromCache();
                break;
            case 3:
                _architecture.PaintFromCache();
                break;
        }
    }

    public void SelectTab(int index)
    {
        if (index >= 0 && index < _tabs.Pages.Count)
            _tabs.SelectedIndex = index;
    }
}
