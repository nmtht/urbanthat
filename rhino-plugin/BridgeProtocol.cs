using System.Text.Json;
using System.Text.Json.Serialization;

namespace UrbanBridge.Plugin;

public sealed record BridgeMessage(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("object")] object? Object = null,
    [property: JsonPropertyName("objects")] IReadOnlyList<object>? Objects = null,
    [property: JsonPropertyName("id")] string? Id = null,
    [property: JsonPropertyName("document_id")] string? DocumentId = null,
    [property: JsonPropertyName("units")] string? Units = null,
    [property: JsonPropertyName("timestamp")] long? Timestamp = null);

public sealed record ObjectPayload(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("layer")] string Layer,
    [property: JsonPropertyName("geometry_type")] string GeometryType,
    [property: JsonPropertyName("mesh")] MeshPayload? Mesh,
    [property: JsonPropertyName("polyline")] PolylinePayload? Polyline,
    [property: JsonPropertyName("attributes")] IReadOnlyDictionary<string, string> Attributes);

public sealed record MeshPayload(
    [property: JsonPropertyName("vertices")] IReadOnlyList<double> Vertices,
    [property: JsonPropertyName("indices")] IReadOnlyList<int> Indices,
    [property: JsonPropertyName("normals")] IReadOnlyList<double>? Normals);

public sealed record PolylinePayload([property: JsonPropertyName("points")] IReadOnlyList<double> Points);

/// <summary>JSON message builders for the WebSocket bridge.</summary>
public static class BridgeProtocol
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static long Ts() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public static string Heartbeat() =>
        JsonSerializer.Serialize(new BridgeMessage("heartbeat", Timestamp: Ts()), Opts);

    public static string ObjectsUpsert(IReadOnlyList<ObjectPayload> objects) =>
        JsonSerializer.Serialize(new BridgeMessage("objects_upsert", Objects: objects.Cast<object>().ToList(), Timestamp: Ts()), Opts);

    public static string ObjectDeleted(Guid id) =>
        JsonSerializer.Serialize(new BridgeMessage("object_deleted", Id: id.ToString(), Timestamp: Ts()), Opts);

    public static string RoadNetworkUpdate(RoadNetworkGraph graph) =>
        JsonSerializer.Serialize(new
        {
            type = "road_network_update",
            timestamp = Ts(),
            edges = graph.Edges.Count,
            nodes = graph.Nodes.Count,
            issues = graph.Issues.Count,
            stats = graph.Stats,
        }, Opts);

    public static string ZoneAnalysisUpdate(ZoneAnalysis analysis) =>
        JsonSerializer.Serialize(new
        {
            type = "zone_analysis_update",
            timestamp = Ts(),
            zones = analysis.Zones.Count,
            issues = analysis.Issues.Count,
            total_area_sqm = analysis.TotalAreaSqm,
            population = analysis.TotalPopulation,
            jobs = analysis.TotalJobs,
        }, Opts);
}
