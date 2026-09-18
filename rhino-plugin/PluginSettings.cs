namespace UrbanBridge.Plugin;

/// <summary>Session-level UI preferences (not persisted to .3dm yet).</summary>
public static class PluginSettings
{
    /// <summary>When true, zone curve edits debounce → regenerate zone proxies.</summary>
    public static bool AutoUpdateGeometry { get; set; }

    /// <summary>Optional closed curve id limiting dashboard / analysis scope.</summary>
    public static Guid? ProjectBoundaryId { get; set; }

    public static bool GenerateFacadesWithMassing { get; set; } = true;

    public static bool GenerateTreesForGreenZones { get; set; } = true;
}
