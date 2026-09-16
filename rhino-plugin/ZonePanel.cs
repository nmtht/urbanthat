using Rhino;
using Rhino.Commands;

namespace UrbanBridge.Plugin;

/// <summary>Command UrbanBridgeZones — opens unified panel on Zoning tab.</summary>
[System.Runtime.InteropServices.Guid("B6E1D8A0-3A2C-4D7B-8A0E-1C5A7B9D4F6C")]
public class UrbanBridgeZonesCommand : Command
{
    public override string EnglishName => "UrbanBridgeZones";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        Rhino.UI.Panels.OpenPanel(typeof(RoadNetworkPanel));
        return Result.Success;
    }
}
