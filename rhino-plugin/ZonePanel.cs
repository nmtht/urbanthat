using Rhino;
using Rhino.Commands;

namespace UrbanBridge.Rhino;

/// <summary>Command UrbanBridgeZones — opens unified panel on Zoning tab.</summary>
[System.Runtime.InteropServices.Guid("B2C3D4E5-F6A7-4B8C-9D0E-1F2A3B4C5D6E")]
public sealed class UrbanBridgeZonesCommand : Command
{
    public override string EnglishName => "UrbanBridgeZones";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        return UrbanBridgeRoadNetworkCommand.OpenUnified(doc, tabIndex: 1);
    }
}
