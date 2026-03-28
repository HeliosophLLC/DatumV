using System.Buffers.Binary;
using System.Numerics;

using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Model.Spatial;

namespace DatumIngest.Functions.Scalar.Spatial;

/// <summary>
/// <c>pc_transform(pc PointCloud, pose Float32[]) → PointCloud</c>. Applies a
/// 4×4 affine transformation matrix to every position in a PointCloud,
/// preserving per-point color. The pose array is 16 Float32 values in
/// row-major order: rows 0–2 hold the rotation+translation; row 3 is
/// conventionally <c>[0, 0, 0, 1]</c> and is ignored (this function is
/// affine-only — no projective division).
/// </summary>
/// <remarks>
/// <para>
/// Layout — <c>pose[0..4]</c> is row 0 <c>[m00 m01 m02 m03]</c>, where
/// <c>m03</c> is the X translation. For a point <c>p = (x, y, z)</c>:
/// </para>
/// <code>
/// p'.x = m00 * x + m01 * y + m02 * z + m03
/// p'.y = m10 * x + m11 * y + m12 * z + m13
/// p'.z = m20 * x + m21 * y + m22 * z + m23
/// </code>
/// <para>
/// Built-in constructors: <see cref="PoseTranslateFunction"/>. Coordinate
/// frame tag is preserved verbatim — the matrix is interpreted as a
/// transform within the cloud's existing frame. Bounding box is recomputed
/// exactly from transformed points (transforming the source bbox corners
/// gives a loose enclosing box for non-axis-aligned rotations).
/// </para>
/// </remarks>
public sealed class PcTransformFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "pc_transform";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Spatial;

    /// <inheritdoc />
    public static string Description =>
        "Applies a 4x4 affine transformation matrix (16-element Float32 array, "
        + "row-major) to every position in a PointCloud. Translation lives in "
        + "the 4th column of rows 0-2; row 3 is ignored. Color/normals "
        + "preserved; coordinate frame tag preserved (the transform is within "
        + "the cloud's existing frame). Bounding box is recomputed exactly.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("pc", DataKindMatcher.Exact(DataKind.PointCloud)),
                new ParameterSpec("pose", DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.PointCloud)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<PcTransformFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef pcArg = arguments.Span[0];
        ValueRef poseArg = arguments.Span[1];

        if (pcArg.IsNull || poseArg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.PointCloud));
        }

        DataValue poseValue = poseArg.ToDataValue(frame.Source);
        ReadOnlySpan<float> pose =
            poseValue.AsArraySpan<float>(frame.Source, frame.SidecarRegistry);
        if (pose.Length != 16)
        {
            throw new FunctionArgumentException(
                Name,
                $"pose array must be exactly 16 Float32 values (a 4x4 row-major matrix); "
                + $"got {pose.Length}.");
        }

        byte[] srcBlob = pcArg.AsPointCloud();
        PointCloudHeader header = PointCloudHeader.Read(srcBlob);
        int pointCount = checked((int)header.PointCount);
        int srcStride = header.PointStrideBytes;
        int srcBase = PointCloudHeader.SizeBytes;

        // Empty cloud → just bounce through with a fresh header (no point
        // payload to walk). Saves the per-point loop and keeps `world =
        // pc_transform(world, pose)` cheap when the accumulator hasn't seen
        // its first contribution yet.
        if (pointCount == 0)
        {
            byte[] passthrough = new byte[PointCloudHeader.SizeBytes];
            header.Write(passthrough);
            return new ValueTask<ValueRef>(ValueRef.FromPointCloud(passthrough));
        }

        // Hoist matrix rows to locals so the inner loop reads from registers
        // instead of indexing the span every iteration.
        float m00 = pose[0],  m01 = pose[1],  m02 = pose[2],  m03 = pose[3];
        float m10 = pose[4],  m11 = pose[5],  m12 = pose[6],  m13 = pose[7];
        float m20 = pose[8],  m21 = pose[9],  m22 = pose[10], m23 = pose[11];
        // Row 3 (pose[12..16]) is ignored — affine-only.

        byte[] outBlob = new byte[srcBlob.Length];
        Span<byte> outSpan = outBlob;
        ReadOnlySpan<byte> srcSpan = srcBlob;

        Vector3 bboxMin = new(float.PositiveInfinity);
        Vector3 bboxMax = new(float.NegativeInfinity);

        for (int i = 0; i < pointCount; i++)
        {
            int slotOffset = srcBase + i * srcStride;
            float x = BinaryPrimitives.ReadSingleLittleEndian(srcSpan.Slice(slotOffset + 0, 4));
            float y = BinaryPrimitives.ReadSingleLittleEndian(srcSpan.Slice(slotOffset + 4, 4));
            float z = BinaryPrimitives.ReadSingleLittleEndian(srcSpan.Slice(slotOffset + 8, 4));

            float xt = m00 * x + m01 * y + m02 * z + m03;
            float yt = m10 * x + m11 * y + m12 * z + m13;
            float zt = m20 * x + m21 * y + m22 * z + m23;

            Span<byte> outSlot = outSpan.Slice(slotOffset, srcStride);
            BinaryPrimitives.WriteSingleLittleEndian(outSlot[0..4], xt);
            BinaryPrimitives.WriteSingleLittleEndian(outSlot[4..8], yt);
            BinaryPrimitives.WriteSingleLittleEndian(outSlot[8..12], zt);

            // Copy remaining attribute bytes (color / normals / intensity)
            // verbatim. Color is an RGBA byte tuple; normals would need
            // rotation if/when this function grows that capability.
            if (srcStride > PointCloudHeader.PositionStrideBytes)
            {
                srcSpan.Slice(slotOffset + 12, srcStride - 12)
                    .CopyTo(outSlot[12..]);
            }

            bboxMin = Vector3.Min(bboxMin, new Vector3(xt, yt, zt));
            bboxMax = Vector3.Max(bboxMax, new Vector3(xt, yt, zt));
        }

        PointCloudHeader outHeader = header with
        {
            BboxMin = bboxMin,
            BboxMax = bboxMax,
        };
        outHeader.Write(outSpan[..PointCloudHeader.SizeBytes]);

        return new ValueTask<ValueRef>(ValueRef.FromPointCloud(outBlob));
    }
}

/// <summary>
/// <c>pose_translate(dx, dy, dz) → Float32[]</c>. Constructs a 4×4 affine
/// translation matrix as a 16-element row-major Float32 array, suitable
/// for passing to <see cref="PcTransformFunction"/>. Saves callers from
/// hand-rolling the array literal at every fold tick.
/// </summary>
public sealed class PoseTranslateFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "pose_translate";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Spatial;

    /// <inheritdoc />
    public static string Description =>
        "Builds a 4x4 affine translation matrix as a 16-element row-major "
        + "Float32 array. Pass to pc_transform to shift a PointCloud by "
        + "(dx, dy, dz). Equivalent to the literal "
        + "[1,0,0,dx, 0,1,0,dy, 0,0,1,dz, 0,0,0,1]::Float32[].";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("dx", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("dy", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("dz", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Float32))),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<PoseTranslateFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull || args[2].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.NullArray(DataKind.Float32));
        }

        float dx = ReadFloat(args[0], "dx");
        float dy = ReadFloat(args[1], "dy");
        float dz = ReadFloat(args[2], "dz");

        float[] matrix =
        [
            1, 0, 0, dx,
            0, 1, 0, dy,
            0, 0, 1, dz,
            0, 0, 0, 1,
        ];
        return new ValueTask<ValueRef>(ValueRef.FromPrimitiveArray(matrix, DataKind.Float32));
    }

    private static float ReadFloat(ValueRef arg, string paramName)
    {
        if (!arg.TryToFloat(out float value))
        {
            throw new FunctionArgumentException(
                Name,
                $"{paramName} of kind {arg.Kind} could not be widened to Float32.");
        }
        return value;
    }
}
