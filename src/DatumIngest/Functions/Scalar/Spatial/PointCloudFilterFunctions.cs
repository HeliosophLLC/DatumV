using System.Buffers.Binary;
using System.Numerics;

using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Model.Spatial;

namespace DatumIngest.Functions.Scalar.Spatial;

/// <summary>
/// <c>pc_filter_depth(pc PointCloud, min_z Float32, max_z Float32) → PointCloud</c>.
/// Keeps only the points whose Z component is within <c>[min_z, max_z]</c>;
/// drops the rest. Output is always unorganized (the dropped points break
/// the grid). Color attributes are preserved verbatim on the kept points;
/// bounding box is recomputed exactly from survivors.
/// </summary>
/// <remarks>
/// <para>
/// The most common use is dropping noisy background points beyond some
/// distance from the camera. Depth models (MiDaS, DPT, Depth-Anything)
/// emit increasingly unreliable Z values at the far end, and per-frame
/// fusion accumulates them as a "ghost wall" of stale geometry. A typical
/// indoor reconstruction calls <c>pc_filter_depth(pc, -3.0, 0.0)</c> (in
/// the OpenGL frame where -z is forward; surfaces closer than 3 m from
/// the camera survive).
/// </para>
/// <para>
/// Note that Z is interpreted in the cloud's *declared coordinate frame*,
/// not a world frame — apply the filter BEFORE <c>pc_transform</c> if you
/// want camera-relative culling, AFTER if you want world-relative culling.
/// Empty result is returned as a 0-point, position-only cloud carrying
/// the source's flags (color flag preserved even when zero points remain).
/// </para>
/// </remarks>
public sealed class PcFilterDepthFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "pc_filter_depth";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Spatial;

    /// <inheritdoc />
    public static string Description =>
        "Keeps only points whose Z is within [min_z, max_z], in the cloud's "
        + "declared coordinate frame. Color attributes preserved verbatim; output "
        + "is always unorganized; bbox recomputed from survivors. Use to drop "
        + "noisy far-depth background points that accumulate as ghost walls "
        + "during multi-frame fusion.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("pc", DataKindMatcher.Exact(DataKind.PointCloud)),
                new ParameterSpec("min_z", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("max_z", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.PointCloud)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<PcFilterDepthFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull || args[2].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.PointCloud));
        }

        if (!args[1].TryToFloat(out float minZ))
        {
            throw new FunctionArgumentException(
                Name, $"min_z of kind {args[1].Kind} could not be widened to Float32.");
        }
        if (!args[2].TryToFloat(out float maxZ))
        {
            throw new FunctionArgumentException(
                Name, $"max_z of kind {args[2].Kind} could not be widened to Float32.");
        }
        if (minZ > maxZ)
        {
            throw new FunctionArgumentException(
                Name, $"min_z ({minZ}) must not exceed max_z ({maxZ}).");
        }

        byte[] srcBlob = args[0].AsPointCloud();
        PointCloudHeader header = PointCloudHeader.Read(srcBlob);
        int srcCount = checked((int)header.PointCount);
        int stride = header.PointStrideBytes;
        int srcBase = PointCloudHeader.SizeBytes;
        ReadOnlySpan<byte> srcSpan = srcBlob;

        // Two-pass: first count survivors so we can size the output blob
        // exactly (no resize/copy), second pass writes them out. For typical
        // depth-filter workloads (drop ~30-70% of points), one extra read of
        // the position bytes is cheaper than provisional growth + trim.
        int keepCount = 0;
        for (int i = 0; i < srcCount; i++)
        {
            float z = BinaryPrimitives.ReadSingleLittleEndian(
                srcSpan.Slice(srcBase + i * stride + 8, 4));
            if (z >= minZ && z <= maxZ)
            {
                keepCount++;
            }
        }

        byte[] outBlob = new byte[PointCloudHeader.SizeBytes + (long)keepCount * stride];
        Span<byte> outSpan = outBlob;

        Vector3 bboxMin = new(float.PositiveInfinity);
        Vector3 bboxMax = new(float.NegativeInfinity);
        int writeOffset = PointCloudHeader.SizeBytes;
        for (int i = 0; i < srcCount; i++)
        {
            int slotOffset = srcBase + i * stride;
            float z = BinaryPrimitives.ReadSingleLittleEndian(srcSpan.Slice(slotOffset + 8, 4));
            if (z < minZ || z > maxZ)
            {
                continue;
            }

            float x = BinaryPrimitives.ReadSingleLittleEndian(srcSpan.Slice(slotOffset + 0, 4));
            float y = BinaryPrimitives.ReadSingleLittleEndian(srcSpan.Slice(slotOffset + 4, 4));

            srcSpan.Slice(slotOffset, stride).CopyTo(outSpan.Slice(writeOffset, stride));
            writeOffset += stride;

            bboxMin = Vector3.Min(bboxMin, new Vector3(x, y, z));
            bboxMax = Vector3.Max(bboxMax, new Vector3(x, y, z));
        }

        // Empty result → keep degenerate bbox at origin so consumers don't
        // see ±Infinity corners (matches pc_empty's convention).
        if (keepCount == 0)
        {
            bboxMin = Vector3.Zero;
            bboxMax = Vector3.Zero;
        }

        PointCloudHeader outHeader = new(
            Version: PointCloudHeader.CurrentVersion,
            Flags: header.Flags,
            CoordinateFrame: header.CoordinateFrame,
            PointCount: (uint)keepCount,
            BboxMin: bboxMin,
            BboxMax: bboxMax,
            Width: 0,    // filtered output is always unorganized
            Height: 0);
        outHeader.Write(outSpan[..PointCloudHeader.SizeBytes]);

        return new ValueTask<ValueRef>(ValueRef.FromPointCloud(outBlob));
    }
}
