using System.Text.Json;
using System.Text.Json.Serialization;

namespace SceneKit;

/// <summary>What a per-part proxy build did (written by the bench, returned by PartProxy for tests): its parts, triangles
/// and draw calls before and after, the deviation it aimed at and the one it reached (metres; null where there is none).</summary>
public sealed record ProxyReport
{
    /// <summary>One part: name, class (tiny, small, thin, bulk), method (voxel, convex, decimated, kept), triangles and
    /// the measured deviation (null: not measured).</summary>
    public sealed record PartEntry
    {
        [JsonPropertyName("name")] public string Name { get; init; } = "";
        [JsonPropertyName("class")] public string Class { get; init; } = "";
        [JsonPropertyName("method")] public string Method { get; init; } = "";
        [JsonPropertyName("tris")] public long Tris { get; init; }
        [JsonPropertyName("dev_m")] public double? DevM { get; init; }
    }

    [JsonPropertyName("object")] public string Object { get; init; } = "";
    [JsonPropertyName("parts")] public int Parts { get; init; }
    [JsonPropertyName("moving_parts")] public int MovingParts { get; init; }
    [JsonPropertyName("tiny_dropped")] public int TinyDropped { get; init; }
    [JsonPropertyName("per_part")] public IReadOnlyList<PartEntry> PerPart { get; init; } = Array.Empty<PartEntry>();
    [JsonPropertyName("tris_before")] public long TrisBefore { get; init; }
    [JsonPropertyName("tris_after")] public long TrisAfter { get; init; }
    [JsonPropertyName("draw_calls_before")] public int DrawCallsBefore { get; init; }
    [JsonPropertyName("draw_calls_after")] public int DrawCallsAfter { get; init; }
    /// <summary>The largest deviation allowed (within mode), null in keep mode.</summary>
    [JsonPropertyName("target_m")] public double? TargetM { get; init; }
    /// <summary>The largest deviation over the parts, null when no part was measured.</summary>
    [JsonPropertyName("reached_m")] public double? ReachedM { get; init; }
    [JsonPropertyName("within")] public bool Within { get; init; }
    [JsonPropertyName("ground")] public string Ground { get; init; } = "full";
    [JsonPropertyName("normals")] public string Normals { get; init; } = "hull";
    [JsonPropertyName("build_ms")] public long BuildMs { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this);
}
