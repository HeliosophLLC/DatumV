using System.Buffers.Binary;
using System.Numerics;

using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Model.Spatial;

namespace DatumIngest.Functions.Scalar.Spatial;

/// <summary>
/// <c>mesh_swap_axes(mesh Mesh, source_axes Int32[]) → Mesh</c>. Applies a
/// signed axis permutation to every vertex position (and per-vertex normal,
/// when present) of a Mesh. Used to convert between coordinate frame
/// conventions — most often the rotation that maps a 3D-reconstruction
/// model's training-time frame (e.g. TripoSR's <c>+X back, +Y right, +Z up</c>)
/// into the engine's exporter / viewer convention
/// (<c>+X right, +Y up, +Z toward viewer</c>).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Axis spec.</strong> A <c>Int32[3]</c> where each entry is in
/// <c>{±1, ±2, ±3}</c>. <c>+1/+2/+3</c> select the source <c>X/Y/Z</c> axis;
/// the sign flips the direction. For each output axis <c>(X, Y, Z)</c>, the
/// corresponding entry tells which source axis (and direction) supplies its
/// value. The three <c>abs(entries)</c> must be a permutation of
/// <c>{1, 2, 3}</c> — no duplicates, no missing axes.
/// </para>
/// <para>
/// <strong>Examples.</strong>
/// <list type="bullet">
///   <item><c>[1, 2, 3]</c> — identity (no-op).</item>
///   <item><c>[2, 3, 1]</c> — cyclic <c>X←Y, Y←Z, Z←X</c>. The
///     <c>TripoSR → Three.js / glTF</c> rotation.</item>
///   <item><c>[1, 3, -2]</c> — Blender Z-up to glTF Y-up
///     (<c>X←X, Y←Z, Z←-Y</c>).</item>
/// </list>
/// </para>
/// <para>
/// <strong>Winding.</strong> If the signed permutation has determinant
/// <c>-1</c> (any reflection — odd permutation of axes, or odd number of
/// sign flips), every triangle's winding is reversed so the mesh's outward
/// normals stay consistent. The header's bbox is recomputed from the
/// transformed positions.
/// </para>
/// <para>
/// <strong>What's preserved.</strong> Vertex colors and UVs are copied
/// verbatim (they don't depend on world-space orientation). Per-vertex
/// normals get the same rotation as positions, so smooth shading still
/// works after the transform.
/// </para>
/// </remarks>
public sealed class MeshSwapAxesFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "mesh_swap_axes";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Applies a signed axis permutation to a Mesh's vertex positions and normals. "
        + "source_axes is an Int32[3] where each entry is in {±1, ±2, ±3} selecting "
        + "which source axis (with optional sign flip) supplies each output axis. "
        + "Use [2, 3, 1] for TripoSR → Three.js / glTF; [1, 3, -2] for Blender Z-up → "
        + "Y-up. Triangle winding is reversed when the permutation has determinant -1.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("mesh",         DataKindMatcher.Exact(DataKind.Mesh)),
                new ParameterSpec("source_axes",  DataKindMatcher.Family(DataKindFamily.IntegerFamily), IsArray: ArrayMatch.Array),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Mesh)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<MeshSwapAxesFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Mesh));
        }
        if (args[1].IsNull)
        {
            throw new FunctionArgumentException(Name, "source_axes must not be NULL.");
        }

        (int[] srcIdx, float[] sign, bool reverseWinding) = ParseAxisSpec(args[1]);

        byte[] srcBlob = args[0].AsMesh();
        MeshHeader src = MeshHeader.Read(srcBlob);

        int vertexCount = checked((int)src.VertexCount);
        int triangleCount = checked((int)src.TriangleCount);
        int stride = src.VertexStrideBytes;
        bool hasNormals = src.HasNormals;
        int normalOffset = MeshHeader.PositionStrideBytes
            + (src.HasColor ? MeshHeader.ColorStrideBytes : 0);

        // Same size as source — axis swap is in-place in terms of layout,
        // only the bytes change (position values, normal values, and
        // possibly index order).
        byte[] dstBlob = new byte[srcBlob.Length];
        ReadOnlySpan<byte> srcSpan = srcBlob;
        Span<byte> dstSpan = dstBlob;

        int vBase = MeshHeader.SizeBytes;
        Vector3 bboxMin = new(float.PositiveInfinity);
        Vector3 bboxMax = new(float.NegativeInfinity);

        for (int v = 0; v < vertexCount; v++)
        {
            int off = vBase + v * stride;

            // Position
            float x = BinaryPrimitives.ReadSingleLittleEndian(srcSpan.Slice(off + 0, 4));
            float y = BinaryPrimitives.ReadSingleLittleEndian(srcSpan.Slice(off + 4, 4));
            float z = BinaryPrimitives.ReadSingleLittleEndian(srcSpan.Slice(off + 8, 4));
            Vector3 outPos = Permute(x, y, z, srcIdx, sign);
            BinaryPrimitives.WriteSingleLittleEndian(dstSpan.Slice(off + 0, 4), outPos.X);
            BinaryPrimitives.WriteSingleLittleEndian(dstSpan.Slice(off + 4, 4), outPos.Y);
            BinaryPrimitives.WriteSingleLittleEndian(dstSpan.Slice(off + 8, 4), outPos.Z);

            bboxMin = Vector3.Min(bboxMin, outPos);
            bboxMax = Vector3.Max(bboxMax, outPos);

            // Color (pass through verbatim)
            if (src.HasColor)
            {
                int colorOff = off + MeshHeader.PositionStrideBytes;
                srcSpan.Slice(colorOff, MeshHeader.ColorStrideBytes)
                    .CopyTo(dstSpan.Slice(colorOff, MeshHeader.ColorStrideBytes));
            }

            // Normal (apply the same rotation as position; sign flips
            // matter for direction so this is the right transform)
            if (hasNormals)
            {
                int nOff = off + normalOffset;
                float nx = BinaryPrimitives.ReadSingleLittleEndian(srcSpan.Slice(nOff + 0, 4));
                float ny = BinaryPrimitives.ReadSingleLittleEndian(srcSpan.Slice(nOff + 4, 4));
                float nz = BinaryPrimitives.ReadSingleLittleEndian(srcSpan.Slice(nOff + 8, 4));
                Vector3 outN = Permute(nx, ny, nz, srcIdx, sign);
                BinaryPrimitives.WriteSingleLittleEndian(dstSpan.Slice(nOff + 0, 4), outN.X);
                BinaryPrimitives.WriteSingleLittleEndian(dstSpan.Slice(nOff + 4, 4), outN.Y);
                BinaryPrimitives.WriteSingleLittleEndian(dstSpan.Slice(nOff + 8, 4), outN.Z);
            }
        }

        // Indices: verbatim copy when the rotation preserves orientation;
        // swap the first and last index of each triangle when it doesn't
        // (so the mesh's outward normals stay consistent under reflection).
        int tBase = vBase + vertexCount * stride;
        int triangleBytes = triangleCount * MeshHeader.IndexStrideBytes;
        if (!reverseWinding)
        {
            srcSpan.Slice(tBase, triangleBytes).CopyTo(dstSpan.Slice(tBase, triangleBytes));
        }
        else
        {
            for (int t = 0; t < triangleCount; t++)
            {
                int tOff = tBase + t * MeshHeader.IndexStrideBytes;
                // a, b, c -> c, b, a (swap first and last index)
                srcSpan.Slice(tOff + 0, 4).CopyTo(dstSpan.Slice(tOff + 8, 4));
                srcSpan.Slice(tOff + 4, 4).CopyTo(dstSpan.Slice(tOff + 4, 4));
                srcSpan.Slice(tOff + 8, 4).CopyTo(dstSpan.Slice(tOff + 0, 4));
            }
        }

        // Zero-vertex meshes: keep the (0,0,0) bbox the source had rather
        // than the sentinel +Inf/-Inf values from the no-update path.
        if (vertexCount == 0)
        {
            bboxMin = bboxMax = Vector3.Zero;
        }

        MeshHeader dst = src with { BboxMin = bboxMin, BboxMax = bboxMax };
        dst.Write(dstSpan[..MeshHeader.SizeBytes]);

        return new ValueTask<ValueRef>(ValueRef.FromMesh(dstBlob));
    }

    /// <summary>
    /// Reads + validates the <c>source_axes</c> argument; returns the
    /// decoded source-index array, sign array, and a flag indicating
    /// whether triangle winding must be reversed for orientation preservation.
    /// </summary>
    private static (int[] srcIdx, float[] sign, bool reverseWinding) ParseAxisSpec(ValueRef arg)
    {
        int[] axes = ReadInt32Array(arg);
        if (axes.Length != 3)
        {
            throw new FunctionArgumentException(Name,
                $"source_axes must be length 3 (one entry per output X/Y/Z); got length {axes.Length}.");
        }
        int[] srcIdx = new int[3];
        float[] sign = new float[3];
        Span<bool> seen = stackalloc bool[3];
        for (int i = 0; i < 3; i++)
        {
            int v = axes[i];
            if (v == 0 || v < -3 || v > 3)
            {
                throw new FunctionArgumentException(Name,
                    "source_axes entries must be in {±1, ±2, ±3} "
                    + $"(±1=X, ±2=Y, ±3=Z); got {v} at position {i}.");
            }
            int axis = System.Math.Abs(v) - 1; // 0/1/2
            if (seen[axis])
            {
                throw new FunctionArgumentException(Name,
                    "source_axes must use each of X / Y / Z exactly once; "
                    + $"axis {"XYZ"[axis]} appears more than once in [{axes[0]}, {axes[1]}, {axes[2]}].");
            }
            seen[axis] = true;
            srcIdx[i] = axis;
            sign[i] = v > 0 ? 1f : -1f;
        }

        // Determinant: parity of the permutation, multiplied by every sign.
        int inversions = 0;
        for (int i = 0; i < 3; i++)
        {
            for (int j = i + 1; j < 3; j++)
            {
                if (srcIdx[i] > srcIdx[j]) inversions++;
            }
        }
        float permSign = (inversions % 2 == 0) ? 1f : -1f;
        float det = permSign * sign[0] * sign[1] * sign[2];
        return (srcIdx, sign, reverseWinding: det < 0f);
    }

    private static Vector3 Permute(float x, float y, float z, int[] srcIdx, float[] sign)
    {
        Span<float> input = stackalloc float[3] { x, y, z };
        return new Vector3(
            sign[0] * input[srcIdx[0]],
            sign[1] * input[srcIdx[1]],
            sign[2] * input[srcIdx[2]]);
    }

    private static int[] ReadInt32Array(ValueRef arg)
    {
        if (!arg.IsArray)
        {
            throw new FunctionArgumentException(Name,
                $"source_axes must be an integer array; got {arg.Kind}.");
        }
        if (arg.Materialized is int[] direct) return direct;
        if (arg.Materialized is long[] longs)
        {
            int[] copied = new int[longs.Length];
            for (int i = 0; i < longs.Length; i++) copied[i] = checked((int)longs[i]);
            return copied;
        }
        ReadOnlySpan<ValueRef> elements = arg.GetArrayElements();
        int[] result = new int[elements.Length];
        for (int i = 0; i < elements.Length; i++)
        {
            if (!elements[i].TryToInt32(out int v))
            {
                throw new FunctionArgumentException(Name,
                    $"source_axes element [{i}] ({elements[i].Kind}) is not coercible to Int32.");
            }
            result[i] = v;
        }
        return result;
    }
}
