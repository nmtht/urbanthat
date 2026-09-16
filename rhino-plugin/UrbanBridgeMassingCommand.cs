using Rhino;
using Rhino.Commands;

namespace UrbanBridge.Rhino;

[System.Runtime.InteropServices.Guid("c4e8a1b2-3d5f-4a7c-9e1b-8f2d6a0c4b7e")]
public class UrbanBridgeMassingCommand : Command
{
    public override string EnglishName => "UrbanBridgeGenerateMassing";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        try
        {
            var server = UrbanBridgePlugin.Instance?.Server;
            if (server is null)
            {
                RhinoApp.WriteLine("[UrbanBridge] Plugin server not running.");
                return Result.Failure;
            }

            server.RebuildZoneAnalysis(doc);
            var analysis = server.LatestZoneAnalysis;
            if (analysis is null || analysis.Zones.Count == 0)
            {
                RhinoApp.WriteLine("[UrbanBridge] No zones. Init closed curves as zones first.");
                return Result.Nothing;
            }

            var gen = new MassingGenerator(doc);
            var batch = gen.Generate(doc, analysis);
            server.LatestMassingBuiltGfaSqm = batch.TotalBuiltFloorAreaSqm;

            RhinoApp.WriteLine(
                $"[UrbanBridge] Massing: created {batch.CreatedCount}, deleted {batch.DeletedCount}, " +
                $"built GFA {batch.TotalBuiltFloorAreaSqm:F0} m²");
            return Result.Success;
        }
        catch (System.Exception ex)
        {
            RhinoApp.WriteLine($"[UrbanBridge] Massing failed: {ex.Message}");
            return Result.Failure;
        }
    }
}
