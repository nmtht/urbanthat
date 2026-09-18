using Eto.Forms;

namespace UrbanBridge.Plugin;

/// <summary>
/// Tabs: Dashboard | Roads | Zoning | Architecture.
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
        _tabs = new TabControl();
        _tabs.Pages.Add(new TabPage { Text = "Dashboard", Content = _dashboard });
        _tabs.Pages.Add(new TabPage { Text = "Roads", Content = _roads });
        _tabs.Pages.Add(new TabPage { Text = "Zoning", Content = _zones });
        _tabs.Pages.Add(new TabPage { Text = "Architecture", Content = _architecture });
        _tabs.SelectedIndexChanged += (_, _) => RefreshActiveTab(fromCacheOnly: true);
        Content = _tabs;
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
