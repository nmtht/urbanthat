namespace UrbanBridge.Plugin;

/// <summary>Session-level UI preferences (not persisted to .3dm yet).</summary>
public static class PluginSettings
{
    /// <summary>When true, edits debounce → regen roads/proxies/massing as appropriate.</summary>
    public static bool AutoUpdateGeometry { get; set; }

    /// <summary>Optional closed curve id limiting dashboard / analysis scope.</summary>
    public static Guid? ProjectBoundaryId { get; set; }

    public static bool GenerateFacadesWithMassing { get; set; } = true;

    public static bool GenerateTreesForGreenZones { get; set; } = true;

    public static bool GreenRoof { get; set; }

    public static bool AutoUpdateMassing { get; set; }
}
