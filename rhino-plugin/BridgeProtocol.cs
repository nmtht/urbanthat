using System.Text.Json.Serialization;

namespace UrbanBridge.Rhino;

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
