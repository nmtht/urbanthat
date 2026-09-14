namespace UrbanBridge.Rhino;

/// <summary>Hard-coded defaults per zone_type (TZ Stage 2.2 §1). Not user-editable in this stage.</summary>
public static class ZoneTypeDefaults
{
    public static readonly string[] ZoneTypes =
    {
        "residential", "commercial", "mixed_use", "industrial", "green", "public",
    };

    public sealed record Defaults(
        double Far,
        double HeightMaxM,
        double GreenRatio,
        double PopulationPerHa,
        double JobsPerHa);

    private static readonly Dictionary<string, Defaults> Table =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // population/jobs densities are people or jobs per hectare
            ["residential"] = new(1.5, 24, 0.25, 250, 0),
            ["commercial"] = new(2.5, 30, 0.10, 0, 400),
            ["mixed_use"] = new(2.0, 27, 0.15, 150, 400), // mixed: 50% pop + 50% jobs — see calculator
            ["industrial"] = new(1.0, 15, 0.10, 0, 150),
            ["green"] = new(0.0, 0, 1.0, 0, 0),
            ["public"] = new(0.8, 12, 0.30, 0, 0),
        };

    public const double DefaultSetbackM = 3.0;

    public static Defaults Get(string zoneType)
    {
        if (Table.TryGetValue(zoneType, out var d))
            return d;
        return Table["residential"];
    }

    public static bool IsKnown(string zoneType) => Table.ContainsKey(zoneType);
}
