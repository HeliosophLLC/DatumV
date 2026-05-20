using System.Buffers.Binary;
using System.Numerics;

using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Model.Spatial;

namespace Heliosoph.DatumV.Functions.Scalar.Spatial;

/// <summary>
/// <c>pc_filter_depth_percentile(pc PointCloud, lower Float32, upper Float32) → PointCloud</c>.
/// Keeps only points whose Z value is within the <c>[lower, upper]</c>
/// quantile of the cloud's own Z distribution. Auto-adapts to whatever
/// scale the source depth used — works identically on normalized inverse
/// depth, metric meters, or any other unit. Useful for portable scripts
/// that must run across multiple depth models without per-model tuning.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Interpretation.</strong> Percentile is measured by Z value
/// (standard statistical convention). In the OpenGL camera frame where
/// −Z is forward, smaller Z values are farther from the camera, so:
/// </para>
/// <list type="bullet">
///   <item><description><c>(0.3, 1.0)</c> — drops the farthest 30% of points (smallest Z), keeps the closest 70%.</description></item>
///   <item><description><c>(0.0, 0.7)</c> — drops the closest 30% of points (largest Z), keeps the farthest 70%.</description></item>
///   <item><description><c>(0.1, 0.9)</c> — drops outliers on both ends (10% closest + 10% farthest). Symmetric outlier rejection.</description></item>
///   <item><description><c>(0.0, 1.0)</c> — no-op; keeps all finite points.</description></item>
/// </list>
/// <para>
/// NaN, +/-Inf, and other non-finite Z values are dropped unconditionally
/// regardless of the percentile bounds (treated as background / failure
/// pixels from the depth model).
/// </para>
/// <para>
/// <strong>Cost.</strong> O(N log N) — one extra pass to collect Z values,
/// one sort to find the percentile thresholds, then the normal filter pass.
/// For ~100K-point clouds this is ~5 ms; well under the cost of the depth
/// model that produced them.
/// </para>
/// </remarks>
public sealed class PcFilterDepthPercentileFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "pc_filter_depth_percentile";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Spatial;

    /// <inheritdoc />
    public static string Description =>
        "Keeps points whose Z is within the [lower, upper] quantile of the cloud's own "
        + "Z distribution. Auto-adapts to any depth scale — works on normalized inverse "
        + "depth, metric meters, or other units. Lower/upper are fractions in [0, 1]. "
        + "(0.3, 1.0) drops the farthest 30%; (0.1, 0.9) is symmetric outlier rejection. "
        + "Non-finite Z values are dropped unconditionally.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("pc", DataKindMatcher.Exact(DataKind.PointCloud)),
                new ParameterSpec("lower", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("upper", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.PointCloud)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<PcFilterDepthPercentileFunction>(argumentKinds);

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

        if (!args[1].TryToFloat(out float lower))
        {
            throw new FunctionArgumentException(
                Name, $"lower of kind {args[1].Kind} could not be widened to Float32.");
        }
        if (!args[2].TryToFloat(out float upper))
        {
            throw new FunctionArgumentException(
                Name, $"upper of kind {args[2].Kind} could not be widened to Float32.");
        }
        if (!(lower >= 0f && lower <= 1f) || !(upper >= 0f && upper <= 1f))
        {
            throw new FunctionArgumentException(
                Name,
                $"lower and upper must be in [0, 1]; got lower={lower}, upper={upper}.");
        }
        if (lower > upper)
        {
            throw new FunctionArgumentException(
                Name,
                $"lower ({lower}) must not exceed upper ({upper}).");
        }

        byte[] srcBlob = args[0].AsPointCloud();
        PointCloudHeader header = PointCloudHeader.Read(srcBlob);
        int srcCount = checked((int)header.PointCount);
        int stride = header.PointStrideBytes;
        int srcBase = PointCloudHeader.SizeBytes;
        ReadOnlySpan<byte> srcSpan = srcBlob;

        if (srcCount == 0)
        {
            byte[] empty = new byte[PointCloudHeader.SizeBytes];
            header.Write(empty);
            return new ValueTask<ValueRef>(ValueRef.FromPointCloud(empty));
        }

        // First pass: extract all finite Z values to compute percentiles.
        // We allocate a separate Float32[] rather than walking the blob's
        // Z values in place because we need to sort them — sort destroys
        // their original positional correspondence.
        float[] zValues = new float[srcCount];
        int finiteCount = 0;
        for (int i = 0; i < srcCount; i++)
        {
            float z = BinaryPrimitives.ReadSingleLittleEndian(
                srcSpan.Slice(srcBase + i * stride + 8, 4));
            if (float.IsFinite(z))
            {
                zValues[finiteCount++] = z;
            }
        }

        if (finiteCount == 0)
        {
            // Every Z is NaN/Inf — empty result.
            byte[] empty = new byte[PointCloudHeader.SizeBytes];
            PointCloudHeader emptyHeader = header with
            {
                PointCount = 0,
                BboxMin = Vector3.Zero,
                BboxMax = Vector3.Zero,
                Width = 0,
                Height = 0,
            };
            emptyHeader.Write(empty);
            return new ValueTask<ValueRef>(ValueRef.FromPointCloud(empty));
        }

        // Sort just the populated prefix; degenerate cases short-circuit
        // (lower=0 and upper=1 means no thresholds to compute, no sort
        // needed — though we still pay the NaN-filtering scan).
        float lowerZ, upperZ;
        if (lower == 0f && upper == 1f)
        {
            lowerZ = float.NegativeInfinity;
            upperZ = float.PositiveInfinity;
        }
        else
        {
            Array.Sort(zValues, 0, finiteCount);
            lowerZ = PercentileAt(zValues, finiteCount, lower);
            upperZ = PercentileAt(zValues, finiteCount, upper);
        }

        // Second pass: count survivors so we can size the output exactly.
        int keepCount = 0;
        for (int i = 0; i < srcCount; i++)
        {
            float z = BinaryPrimitives.ReadSingleLittleEndian(
                srcSpan.Slice(srcBase + i * stride + 8, 4));
            if (float.IsFinite(z) && z >= lowerZ && z <= upperZ) keepCount++;
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
            if (!float.IsFinite(z) || z < lowerZ || z > upperZ) continue;

            float x = BinaryPrimitives.ReadSingleLittleEndian(srcSpan.Slice(slotOffset + 0, 4));
            float y = BinaryPrimitives.ReadSingleLittleEndian(srcSpan.Slice(slotOffset + 4, 4));
            srcSpan.Slice(slotOffset, stride).CopyTo(outSpan.Slice(writeOffset, stride));
            writeOffset += stride;
            bboxMin = Vector3.Min(bboxMin, new Vector3(x, y, z));
            bboxMax = Vector3.Max(bboxMax, new Vector3(x, y, z));
        }

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

    /// <summary>
    /// Returns the value at the given fractional position in a sorted array.
    /// Uses linear interpolation between adjacent ranks (standard percentile
    /// convention, matches numpy's <c>linear</c> method). For <c>fraction = 0</c>
    /// returns the minimum; for <c>fraction = 1</c> returns the maximum.
    /// </summary>
    private static float PercentileAt(float[] sortedValues, int count, float fraction)
    {
        if (count == 1) return sortedValues[0];
        if (fraction <= 0f) return sortedValues[0];
        if (fraction >= 1f) return sortedValues[count - 1];

        float rank = fraction * (count - 1);
        int lowerIdx = (int)MathF.Floor(rank);
        int upperIdx = lowerIdx + 1;
        float t = rank - lowerIdx;
        return sortedValues[lowerIdx] * (1f - t) + sortedValues[upperIdx] * t;
    }
}
