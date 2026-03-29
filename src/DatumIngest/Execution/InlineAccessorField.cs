using DatumIngest.Model;

namespace DatumIngest.Execution;

/// <summary>
/// Identifies an inline-metadata field on <see cref="DataValue"/> that can
/// be read directly from the struct's per-kind payload bytes without
/// dispatching through <see cref="DatumIngest.Functions.IScalarFunction.ExecuteAsync"/>.
/// One value per supported accessor function; the elider matches function
/// calls against this enum via the
/// <see cref="DatumIngest.Functions.IInlineMetadataAccessor"/> marker.
/// </summary>
/// <remarks>
/// New entries arrive in pairs with their owning function class: extend
/// this enum, add a descriptor in
/// <see cref="InlineAccessorDescriptors"/>, then have the function
/// implement <see cref="DatumIngest.Functions.IInlineMetadataAccessor"/>
/// and return the new value from its <c>Field</c> getter.
/// </remarks>
public enum InlineAccessorField
{
    /// <summary><c>image_width</c> — low 16 bits of <c>_p4</c> on an <see cref="DataKind.Image"/> value.</summary>
    ImageWidth,

    /// <summary><c>image_height</c> — high 16 bits of <c>_p4</c> on an <see cref="DataKind.Image"/> value.</summary>
    ImageHeight,

    /// <summary><c>audio_sample_rate</c> — full <c>_p4</c> on an <see cref="DataKind.Audio"/> value.</summary>
    AudioSampleRate,

    /// <summary><c>video_width</c> — low 16 bits of <c>_p4</c> on a <see cref="DataKind.Video"/> value.</summary>
    VideoWidth,

    /// <summary><c>video_height</c> — high 16 bits of <c>_p4</c> on a <see cref="DataKind.Video"/> value.</summary>
    VideoHeight,

    /// <summary><c>point_cloud_count</c> — full <c>_p4</c> on a <see cref="DataKind.PointCloud"/> value.</summary>
    PointCloudCount,

    /// <summary><c>point_cloud_has_color</c> — low byte of <c>_p5</c> on a <see cref="DataKind.PointCloud"/> value (HasColor flag bit).</summary>
    PointCloudHasColor,

    /// <summary><c>mesh_vertex_count</c> — full <c>_p4</c> on a <see cref="DataKind.Mesh"/> value.</summary>
    MeshVertexCount,

    /// <summary><c>mesh_triangle_count</c> — full <c>_p5</c> on a <see cref="DataKind.Mesh"/> value.</summary>
    MeshTriangleCount,

    /// <summary>
    /// <c>octet_length</c> — UTF-8 byte length of a <see cref="DataKind.String"/>
    /// value. Inline strings carry it in the low byte of <c>_charCount</c>; the
    /// elider's fast path returns it directly. Non-inline strings reach the
    /// evaluator with the cache discarded (the <see cref="Functions.ValueRef"/>
    /// carries a managed string), so the fast path falls back to the function's
    /// char-walk.
    /// </summary>
    StringByteLength,

    /// <summary>
    /// <c>length</c> — Unicode code-point count of a <see cref="DataKind.String"/>
    /// value (PG <c>length(text)</c> semantics — surrogate-pair characters count
    /// as 1). Inline strings carry the count in the high byte of <c>_charCount</c>;
    /// non-inline strings fall back to a <see cref="System.Text.Rune"/> walk.
    /// </summary>
    StringCodePointLength,
}

/// <summary>
/// Static per-<see cref="InlineAccessorField"/> metadata consumed by the
/// elider when building <see cref="InlineAccessorExpression"/> nodes and
/// by the evaluator when reading them. Each descriptor captures the
/// argument's expected <see cref="DataKind"/>, the result kind, and the
/// canonical SQL function name (so the evaluator can locate the fallback
/// <see cref="DatumIngest.Functions.IScalarFunction"/> when the inline
/// metadata is unstamped).
/// </summary>
public static class InlineAccessorDescriptors
{
    /// <summary>
    /// Per-<see cref="InlineAccessorField"/> static descriptor.
    /// </summary>
    /// <param name="Field">The field this descriptor describes.</param>
    /// <param name="ArgumentKind">Expected <see cref="DataKind"/> of the single argument.</param>
    /// <param name="ResultKind">The function's declared return kind.</param>
    /// <param name="FunctionName">Canonical SQL function name, used to recover the fallback <see cref="DatumIngest.Functions.IScalarFunction"/>.</param>
    public sealed record Descriptor(
        InlineAccessorField Field,
        DataKind ArgumentKind,
        DataKind ResultKind,
        string FunctionName);

    private static readonly Descriptor[] _byField =
    [
        new(InlineAccessorField.ImageWidth, DataKind.Image, DataKind.Int32, "image_width"),
        new(InlineAccessorField.ImageHeight, DataKind.Image, DataKind.Int32, "image_height"),
        new(InlineAccessorField.AudioSampleRate, DataKind.Audio, DataKind.Int32, "audio_sample_rate"),
        new(InlineAccessorField.VideoWidth, DataKind.Video, DataKind.Int32, "video_width"),
        new(InlineAccessorField.VideoHeight, DataKind.Video, DataKind.Int32, "video_height"),
        new(InlineAccessorField.PointCloudCount, DataKind.PointCloud, DataKind.Int32, "point_cloud_count"),
        new(InlineAccessorField.PointCloudHasColor, DataKind.PointCloud, DataKind.Boolean, "point_cloud_has_color"),
        new(InlineAccessorField.MeshVertexCount, DataKind.Mesh, DataKind.Int32, "mesh_vertex_count"),
        new(InlineAccessorField.MeshTriangleCount, DataKind.Mesh, DataKind.Int32, "mesh_triangle_count"),
        new(InlineAccessorField.StringByteLength, DataKind.String, DataKind.Int32, "octet_length"),
        new(InlineAccessorField.StringCodePointLength, DataKind.String, DataKind.Int32, "length"),
    ];

    /// <summary>
    /// Returns the descriptor for <paramref name="field"/>.
    /// </summary>
    public static Descriptor Get(InlineAccessorField field) => _byField[(int)field];
}
