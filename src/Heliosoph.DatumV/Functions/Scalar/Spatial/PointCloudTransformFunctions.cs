using System.Buffers.Binary;
using System.Numerics;

using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Model.Spatial;

namespace Heliosoph.DatumV.Functions.Scalar.Spatial;

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
                new ParameterSpec("pose", DataKindMatcher.Family(DataKindFamily.FloatFamily), IsArray: ArrayMatch.Array),
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

        float[] pose = PoseMatrixArgument.Read16(poseArg, frame, Name, "pose");

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

/// <summary>
/// <c>pose_identity() → Float32[]</c>. Returns the 4×4 identity matrix as a
/// 16-element row-major Float32 array. Use as the seed value of a recursive
/// CTE that accumulates per-frame pose composition.
/// </summary>
public sealed class PoseIdentityFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "pose_identity";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Spatial;

    /// <inheritdoc />
    public static string Description =>
        "Returns the 4x4 identity matrix as a 16-element row-major Float32 "
        + "array. Equivalent to the literal [1,0,0,0, 0,1,0,0, 0,0,1,0, "
        + "0,0,0,1]::Float32[]. Use as the seed of a recursive CTE that "
        + "accumulates frame-to-frame pose via pose_compose, or as the "
        + "no-op pose for pc_transform.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Float32))),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<PoseIdentityFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        float[] matrix =
        [
            1, 0, 0, 0,
            0, 1, 0, 0,
            0, 0, 1, 0,
            0, 0, 0, 1,
        ];
        return new ValueTask<ValueRef>(ValueRef.FromPrimitiveArray(matrix, DataKind.Float32));
    }
}

/// <summary>
/// Reads a 16-element pose-matrix argument as <see cref="float"/> values,
/// accepting <c>Float32[]</c> (the pose family's native kind) or
/// <c>Float64[]</c> — the kind window aggregates like <c>AVG</c> produce
/// when smoothing a pose trajectory. Float64 input is narrowed elementwise.
/// </summary>
internal static class PoseMatrixArgument
{
    public static float[] Read16(
        ValueRef arg, EvaluationFrame frame, string functionName, string paramName)
        => Read16(arg.ToDataValue(frame.Source), frame.Source, frame.SidecarRegistry, functionName, paramName);

    public static float[] Read16(
        DataValue value,
        IValueStore source,
        DatumFile.Sidecar.SidecarRegistry? registry,
        string functionName,
        string paramName)
    {
        if (value.Kind == DataKind.Float32)
        {
            ReadOnlySpan<float> span = value.AsArraySpan<float>(source, registry);
            EnsureLength(span.Length, functionName, paramName);
            return span.ToArray();
        }
        if (value.Kind == DataKind.Float64)
        {
            ReadOnlySpan<double> span = value.AsArraySpan<double>(source, registry);
            EnsureLength(span.Length, functionName, paramName);
            float[] narrowed = new float[16];
            for (int i = 0; i < 16; i++)
            {
                narrowed[i] = (float)span[i];
            }
            return narrowed;
        }
        throw new FunctionArgumentException(
            functionName,
            $"{paramName} must be a Float32[] or Float64[] pose matrix; got {value.Kind}[].");
    }

    private static void EnsureLength(int length, string functionName, string paramName)
    {
        if (length != 16)
        {
            throw new FunctionArgumentException(
                functionName,
                $"{paramName} must be exactly 16 float values (a 4x4 row-major matrix); got {length}.");
        }
    }
}

/// <summary>
/// <c>pose_compose(a Float32[]|Float64[], b Float32[]|Float64[]) → Float32[]</c>.
/// Multiplies two 4×4 row-major pose matrices: <c>result = a · b</c>. Use to
/// accumulate frame-to-frame poses recovered by <see cref="PoseFromRgbdFunction"/>
/// into a single transform that lands a later frame's cloud in an earlier
/// frame's coordinate system. Float64 inputs (e.g. a pose trajectory smoothed
/// with <c>AVG(…) OVER (…)</c> window aggregates) are narrowed elementwise;
/// the result is always <c>Float32[]</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Order matters.</strong> Matrix multiplication is not commutative.
/// For chained reconstruction across frames 0, 1, …, N:
/// </para>
/// <code>
/// accumulated_N = accumulated_(N-1) · pose_from_rgbd(frame_(N-1), frame_N)
/// </code>
/// <para>
/// The "step" pose goes on the RIGHT — applying <c>accumulated_N</c> to a
/// point in frame N's local space yields its position in frame 0's space.
/// Reversing the order produces a different (incorrect) transform.
/// </para>
/// <para>
/// <strong>Affine only.</strong> Both inputs are treated as affine 4×4
/// matrices; the bottom row of each is assumed to be <c>[0, 0, 0, 1]</c>
/// and the output is constructed with that row verbatim. This matches the
/// convention used by <see cref="PcTransformFunction"/> — pose matrices
/// here never carry a projective component.
/// </para>
/// </remarks>
public sealed class PoseComposeFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "pose_compose";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Spatial;

    /// <inheritdoc />
    public static string Description =>
        "Multiplies two 4x4 row-major pose matrices (16-element Float32 or "
        + "Float64 arrays): result = a * b. Use to accumulate per-frame poses "
        + "from pose_from_rgbd into a cumulative transform for chained "
        + "reconstruction. Order is significant: for accumulated_N = "
        + "accumulated_(N-1) * step_N, the cumulative pose goes on the left "
        + "and the new single-step pose on the right. Float64 inputs (e.g. "
        + "AVG-smoothed trajectories) are narrowed; output is always "
        + "Float32[]. Affine-only — bottom row is set to [0,0,0,1].";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("a", DataKindMatcher.Family(DataKindFamily.FloatFamily), IsArray: ArrayMatch.Array),
                new ParameterSpec("b", DataKindMatcher.Family(DataKindFamily.FloatFamily), IsArray: ArrayMatch.Array),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Float32))),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<PoseComposeFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef aArg = arguments.Span[0];
        ValueRef bArg = arguments.Span[1];
        if (aArg.IsNull || bArg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.NullArray(DataKind.Float32));
        }

        float[] a = PoseMatrixArgument.Read16(aArg, frame, Name, "a");
        float[] b = PoseMatrixArgument.Read16(bArg, frame, Name, "b");

        // Affine-only: read 3x4 from each, fix bottom row to [0,0,0,1].
        // Hoist a's rows to locals so the inner accumulation stays in registers.
        float a00 = a[0],  a01 = a[1],  a02 = a[2],  a03 = a[3];
        float a10 = a[4],  a11 = a[5],  a12 = a[6],  a13 = a[7];
        float a20 = a[8],  a21 = a[9],  a22 = a[10], a23 = a[11];

        float b00 = b[0],  b01 = b[1],  b02 = b[2],  b03 = b[3];
        float b10 = b[4],  b11 = b[5],  b12 = b[6],  b13 = b[7];
        float b20 = b[8],  b21 = b[9],  b22 = b[10], b23 = b[11];

        float[] r =
        [
            a00 * b00 + a01 * b10 + a02 * b20,
            a00 * b01 + a01 * b11 + a02 * b21,
            a00 * b02 + a01 * b12 + a02 * b22,
            a00 * b03 + a01 * b13 + a02 * b23 + a03,

            a10 * b00 + a11 * b10 + a12 * b20,
            a10 * b01 + a11 * b11 + a12 * b21,
            a10 * b02 + a11 * b12 + a12 * b22,
            a10 * b03 + a11 * b13 + a12 * b23 + a13,

            a20 * b00 + a21 * b10 + a22 * b20,
            a20 * b01 + a21 * b11 + a22 * b21,
            a20 * b02 + a21 * b12 + a22 * b22,
            a20 * b03 + a21 * b13 + a22 * b23 + a23,

            0, 0, 0, 1,
        ];

        return new ValueTask<ValueRef>(ValueRef.FromPrimitiveArray(r, DataKind.Float32));
    }
}

/// <summary>
/// <c>pose_inverse(pose Float32[]) → Float32[]</c>. Computes the inverse of
/// a 4×4 affine pose matrix using the rigid-inverse formula
/// <c>M⁻¹ = [Rᵀ | −Rᵀ·t]</c>. Cheap (no general matrix inversion) and
/// numerically stable, but assumes the rotation block is orthogonal.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Primary use case</strong>: convention diagnosis. When
/// <c>pc_transform(cloud, cumulative_pose)</c> produces an unexpected
/// result, swap in <c>pc_transform(cloud, pose_inverse(cumulative_pose))</c>
/// — whichever direction lands the per-frame clouds in a coherent world
/// frame tells you which convention your pose-estimation pipeline emits.
/// </para>
/// <para>
/// <strong>Rigid-inverse assumption.</strong> For a rotation+translation
/// matrix <c>M = [R | t; 0 | 1]</c> with orthogonal <c>R</c> (<c>RᵀR = I</c>),
/// the inverse simplifies to <c>M⁻¹ = [Rᵀ | −Rᵀ·t; 0 | 1]</c>. Pose
/// matrices from RGBD pose estimation typically satisfy this with small
/// numerical drift — the rigid formula is correct to within rounding.
/// For poses with non-orthogonal rotation (anisotropic scale, shear,
/// projective components), use a general 4×4 matrix inverse instead;
/// this primitive will return a "close but not exact" result.
/// </para>
/// <para>
/// <strong>Bottom row.</strong> Output's bottom row is set to
/// <c>[0, 0, 0, 1]</c>, matching <see cref="PcTransformFunction"/>'s
/// affine-only convention. The input's bottom row is ignored.
/// </para>
/// </remarks>
public sealed class PoseInverseFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "pose_inverse";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Spatial;

    /// <inheritdoc />
    public static string Description =>
        "Inverts a 4x4 affine pose matrix using the rigid-inverse formula "
        + "M^-1 = [R^T | -R^T·t]. Assumes orthogonal rotation; correct to "
        + "within numerical drift for typical pose-estimation output. Use to "
        + "swap pose convention (camera-to-world ↔ world-to-camera) when "
        + "diagnosing whether your pose pipeline emits the direction "
        + "pc_transform expects.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("pose", DataKindMatcher.Family(DataKindFamily.FloatFamily), IsArray: ArrayMatch.Array),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Float32))),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<PoseInverseFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef arg = arguments.Span[0];
        if (arg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.NullArray(DataKind.Float32));
        }

        float[] pose = PoseMatrixArgument.Read16(arg, frame, Name, "pose");

        // Row-major layout. pose[0..4] = [r00 r01 r02 tx], etc.
        float r00 = pose[0],  r01 = pose[1],  r02 = pose[2],  tx = pose[3];
        float r10 = pose[4],  r11 = pose[5],  r12 = pose[6],  ty = pose[7];
        float r20 = pose[8],  r21 = pose[9],  r22 = pose[10], tz = pose[11];

        // Inverse translation: -R^T · t. R^T has rows (r00, r10, r20),
        // (r01, r11, r21), (r02, r12, r22). The dot product of each row with
        // (tx, ty, tz) gives the corresponding translation component.
        float itx = -(r00 * tx + r10 * ty + r20 * tz);
        float ity = -(r01 * tx + r11 * ty + r21 * tz);
        float itz = -(r02 * tx + r12 * ty + r22 * tz);

        float[] inv =
        [
            r00, r10, r20, itx,
            r01, r11, r21, ity,
            r02, r12, r22, itz,
            0,   0,   0,   1,
        ];
        return new ValueTask<ValueRef>(ValueRef.FromPrimitiveArray(inv, DataKind.Float32));
    }
}

/// <summary>
/// <c>pose_to_world_position(pose Float32[]) → Point3D</c>. Extracts the
/// camera's world-space position from a camera-to-world pose matrix —
/// i.e. the translation component of the 4×4 row-major matrix. Useful for
/// visualizing camera trajectories (aggregate per-frame positions into a
/// PointCloud and overlay on the scene to see where the camera moved).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Convention assumption.</strong> Assumes <paramref>pose</paramref>
/// is a <em>camera-to-world</em> transform — i.e. applying it to the camera-
/// local origin <c>(0, 0, 0)</c> yields the camera's position in the world
/// frame. That's just the translation triplet at indices [3], [7], [11] of
/// the row-major matrix. If your pose convention is world-to-camera, run
/// it through <see cref="PoseInverseFunction"/> first.
/// </para>
/// <para>
/// <strong>Diagnostic value.</strong> Aggregating <c>pose_to_world_position</c>
/// across all frames produces a sparse "trajectory" point set. For a phone
/// pan from a fixed standing position, this should be a tight cluster
/// (~10cm of wrist sway). For a video walking through a scene, it should
/// be a continuous line of points. Visually disambiguates rotation-only vs
/// translation-bearing motion in your pose pipeline.
/// </para>
/// </remarks>
public sealed class PoseToWorldPositionFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "pose_to_world_position";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Spatial;

    /// <inheritdoc />
    public static string Description =>
        "Extracts the camera's world-space position from a camera-to-world "
        + "4x4 pose matrix — just the translation triplet at indices [3], [7], "
        + "[11]. Aggregate across frames to visualize a camera trajectory.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("pose", DataKindMatcher.Family(DataKindFamily.FloatFamily), IsArray: ArrayMatch.Array),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Point3D)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<PoseToWorldPositionFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef arg = arguments.Span[0];
        if (arg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Point3D));
        }

        float[] pose = PoseMatrixArgument.Read16(arg, frame, Name, "pose");

        // Row-major translation triplet: m03, m13, m23.
        return new ValueTask<ValueRef>(
            ValueRef.FromPoint3D(new Vector3(pose[3], pose[7], pose[11])));
    }
}

/// <summary>
/// <c>pose_to_euler_degrees(pose Float32[]) → Float32[3]</c>. Extracts the
/// rotation portion of a 4×4 pose matrix as <c>(yaw, pitch, roll)</c>
/// Tait-Bryan angles in <strong>degrees</strong>. Companion to
/// <see cref="PoseToWorldPositionFunction"/> for understanding what a pose
/// pipeline is producing — for a phone pan, you'd expect yaw to drift
/// smoothly while pitch and roll stay near zero.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Convention.</strong> Tait-Bryan with intrinsic Y-X-Z ordering —
/// rotate around the world's Y axis first (yaw), then the rotated X axis
/// (pitch), then the rotated Z axis (roll). Matches OpenGL / Three.js
/// where +Y is up, so yaw is "turning around the vertical axis" — the
/// dominant component of a phone pan.
/// </para>
/// <para>
/// <strong>Output layout.</strong> <c>[yaw, pitch, roll]</c> in degrees,
/// accessed via <c>array_get(result, 1)</c>, <c>array_get(result, 2)</c>,
/// <c>array_get(result, 3)</c>. Each is in <c>[-180, 180]</c> for yaw and
/// roll, <c>[-90, 90]</c> for pitch.
/// </para>
/// <para>
/// <strong>Gimbal lock.</strong> When pitch approaches ±90° (camera
/// pointing nearly straight up or down), yaw and roll become degenerate
/// (multiple valid decompositions). This implementation attributes the
/// combined rotation to yaw and zeroes out roll in that case — keeps
/// trajectory analysis stable.
/// </para>
/// </remarks>
public sealed class PoseToEulerDegreesFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "pose_to_euler_degrees";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Spatial;

    /// <inheritdoc />
    public static string Description =>
        "Extracts (yaw, pitch, roll) Tait-Bryan angles in degrees from a 4x4 "
        + "pose matrix. Returns a 3-element Float32 array. Y-X-Z intrinsic "
        + "ordering — yaw is around the vertical (Y) axis, the dominant "
        + "component of a phone pan.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("pose", DataKindMatcher.Family(DataKindFamily.FloatFamily), IsArray: ArrayMatch.Array),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Float32))),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<PoseToEulerDegreesFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef arg = arguments.Span[0];
        if (arg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.NullArray(DataKind.Float32));
        }

        float[] pose = PoseMatrixArgument.Read16(arg, frame, Name, "pose");

        // Row-major rotation block. Y-X-Z intrinsic extraction:
        //   yaw   = atan2(r02, r22)
        //   pitch = asin(-r12)
        //   roll  = atan2(r10, r11)
        float r00 = pose[0];
        float r02 = pose[2];
        float r10 = pose[4];
        float r11 = pose[5];
        float r12 = pose[6];
        float r20 = pose[8];
        float r22 = pose[10];

        // Clamp the pitch input — float drift can push it slightly outside
        // [-1, 1] and Asin would return NaN at the boundary.
        float pitchSin = -r12;
        if (pitchSin > 1f) pitchSin = 1f;
        if (pitchSin < -1f) pitchSin = -1f;
        float pitch = MathF.Asin(pitchSin);

        float yaw, roll;
        // Near gimbal lock the standard formula goes degenerate (both r02 and
        // r22 → 0, atan2 unstable). Use the alternate decomposition that
        // routes everything through yaw.
        const float GimbalThreshold = 0.99999f;
        if (MathF.Abs(pitchSin) > GimbalThreshold)
        {
            yaw = MathF.Atan2(-r20, r00);
            roll = 0f;
        }
        else
        {
            yaw = MathF.Atan2(r02, r22);
            roll = MathF.Atan2(r10, r11);
        }

        const float RadToDeg = 180f / MathF.PI;
        float[] result =
        [
            yaw * RadToDeg,
            pitch * RadToDeg,
            roll * RadToDeg,
        ];
        return new ValueTask<ValueRef>(ValueRef.FromPrimitiveArray(result, DataKind.Float32));
    }
}
