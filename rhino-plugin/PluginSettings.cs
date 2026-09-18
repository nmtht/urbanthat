namespace UrbanBridge.Plugin;

/// <summary>Session-level UI preferences (not persisted to .3dm yet).</summary>
public static class PluginSettings
{
    /// <summary>
    /// When true, document edits debounce → regen road surfaces, zone proxies,
    /// massing, courtyards, facades, trees (full pipeline).
    /// </summary>
    public static bool AutoUpdateGeometry { get; set; }

    /// <summary>Optional closed curve id limiting dashboard / analysis scope.</summary>
    public static Guid? ProjectBoundaryId { get; set; }

    public static bool GenerateFacadesWithMassing { get; set; } = true;

    public static bool GenerateTreesForGreenZones { get; set; } = true;

    public static bool GreenRoof { get; set; }

    /// <summary>Deprecated alias — always follows AutoUpdateGeometry.</summary>
    public static bool AutoUpdateMassing
    {
        get => AutoUpdateGeometry;
        set => AutoUpdateGeometry = value;
    }
}
