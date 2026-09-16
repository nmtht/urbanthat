using Rhino;
using Rhino.Commands;

namespace UrbanBridge.Plugin;

/// <summary>Command UrbanBridgeDashboard — opens unified panel on Dashboard tab.</summary>
[System.Runtime.InteropServices.Guid("C7F2E9A1-4B3D-4E8C-9A1F-2D6B8C0E5A7D")]
public class UrbanBridgeDashboardCommand : Command
{
    public override string EnglishName => "UrbanBridgeDashboard";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        Rhino.UI.Panels.OpenPanel(typeof(RoadNetworkPanel));
        return Result.Success;
    }
}
