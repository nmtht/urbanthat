using Rhino;
using Rhino.Commands;

namespace UrbanBridge.Rhino;

/// <summary>Command UrbanBridgeDashboard — opens unified panel on Dashboard tab.</summary>
[System.Runtime.InteropServices.Guid("C3D4E5F6-A7B8-4C9D-0E1F-2A3B4C5D6E7F")]
public sealed class UrbanBridgeDashboardCommand : Command
{
    public override string EnglishName => "UrbanBridgeDashboard";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        return UrbanBridgeRoadNetworkCommand.OpenUnified(doc, tabIndex: 2);
    }
}
