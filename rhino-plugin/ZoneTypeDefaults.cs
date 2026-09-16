namespace UrbanBridge.Plugin;

/// <summary>Default FAR / height / green ratio by zone_type.</summary>
public static class ZoneTypeDefaults
{
    public const double DefaultSetbackM = 3.0;

    public static readonly string[] ZoneTypes =
    {
        "residential", "commercial", "mixed_use", "industrial", "green", "public",
    };

    public readonly record struct Defaults(double Far, double HeightMaxM, double GreenRatio);

    public static Defaults Get(string zoneType) => zoneType.ToLowerInvariant() switch
    {
        "residential" => new(1.5, 24, 0.25),
        "commercial" => new(2.5, 36, 0.10),
        "mixed_use" => new(2.0, 30, 0.15),
        "industrial" => new(1.0, 18, 0.05),
        "green" => new(0.0, 0, 1.0),
        "public" => new(0.5, 15, 0.30),
        _ => new(1.5, 24, 0.25),
    };

    public static bool IsKnown(string zoneType) =>
        ZoneTypes.Any(t => t.Equals(zoneType, StringComparison.OrdinalIgnoreCase));
}
