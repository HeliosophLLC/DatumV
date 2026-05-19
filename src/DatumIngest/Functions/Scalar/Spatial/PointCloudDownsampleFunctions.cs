using System.Buffers.Binary;
using System.Numerics;

using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Model.Spatial;

namespace Heliosoph.DatumV.Functions.Scalar.Spatial;

/// <summary>
/// <c>pc_voxel_downsample(pc PointCloud, cell_size Float32) → PointCloud</c>.
/// Snaps every point to a regular 3D grid of <paramref>cell_size</paramref>-sided
/// cubes, keeps one representative per occupied cell. Output is always
/// unorganized, with point count bounded by
/// <c>floor((bbox_extent / cell_size + 1)³)</c>. Color (when present) is
/// per-cell averaged.
/// </summary>
/// <remarks>
/// <para>
/// Idempotent: <c>pc_voxel_downsample(pc_voxel_downsample(pc, c), c)</c>
/// equals <c>pc_voxel_downsample(pc, c)</c> (modulo floating-point order of
/// addition). Safe to run repeatedly — useful inside a SCAN fold to bound
/// accumulator growth.
/// </para>
/// <para>
/// Per-cell representative: the centroid of the cell's contributing points,
/// not an arbitrary one. Color is the component-wise mean (gamma-naive —
/// fine for visualisation, biased darker than perceptually correct mean
/// for high-contrast cells; revisit if a non-display consumer cares).
/// </para>
/// <para>
/// Cell-index hashing uses the *world-space* position (anchored at origin
/// 0,0,0), so the same world-space point lands in the same cell regardless
/// of which cloud it came from. This matters for cross-frame fusion: a
/// surface seen by frames N and N+1 must hash into the same cell either way,
/// otherwise the dedup property breaks.
/// </para>
/// </remarks>
public sealed class PcVoxelDownsampleFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "pc_voxel_downsample";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Spatial;

    /// <inheritdoc />
    public static string Description =>
        "Snaps every point to a regular 3D grid (cell_size-sided cubes) and emits "
        + "one representative per occupied cell — the centroid of the cell's "
        + "contributing points, with per-component color average when color is "
        + "present. Output is always unorganized; point count is bounded by "
        + "(bbox_extent / cell_size)³. Idempotent — safe to apply repeatedly "
        + "inside a SCAN fold to bound accumulator growth.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("pc", DataKindMatcher.Exact(DataKind.PointCloud)),
                new ParameterSpec("cell_size", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.PointCloud)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<PcVoxelDownsampleFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.PointCloud));
        }

        if (!args[1].TryToFloat(out float cellSize) || !(cellSize > 0f) || !float.IsFinite(cellSize))
        {
            throw new FunctionArgumentException(
                Name,
                $"cell_size must be a positive finite Float32; got {(args[1].TryToFloat(out float c) ? c.ToString() : args[1].Kind.ToString())}.");
        }

        byte[] srcBlob = args[0].AsPointCloud();
        PointCloudHeader header = PointCloudHeader.Read(srcBlob);
        int srcCount = checked((int)header.PointCount);
        int stride = header.PointStrideBytes;
        int srcBase = PointCloudHeader.SizeBytes;
        bool hasColor = header.HasColor;
        ReadOnlySpan<byte> srcSpan = srcBlob;

        // Empty input → empty output, no work.
        if (srcCount == 0)
        {
            byte[] empty = new byte[PointCloudHeader.SizeBytes];
            header.Write(empty);
            return new ValueTask<ValueRef>(ValueRef.FromPointCloud(empty));
        }

        // Cell index is computed in world-space (anchored at origin 0,0,0)
        // so the same world-space position always lands in the same cell,
        // regardless of which cloud it's part of. Critical for cross-cloud
        // dedup in multi-frame fusion. Computation done in double precision
        // to avoid (x * inv) landing inconsistently on cell boundaries when
        // x is a Float32 with a representation that rounds either way.
        double invD = 1.0 / cellSize;

        // Accumulator per occupied cell. Cell key = packed (i, j, k) into a
        // 64-bit hash key (21 bits per axis, signed-shifted). Plenty for
        // realistic indoor scenes; if a coordinate exceeds ±1M cells we
        // throw — that's a workload we're not designing for.
        Dictionary<long, CellAccumulator> cells = new();
        for (int i = 0; i < srcCount; i++)
        {
            int slotOffset = srcBase + i * stride;
            float x = BinaryPrimitives.ReadSingleLittleEndian(srcSpan.Slice(slotOffset + 0, 4));
            float y = BinaryPrimitives.ReadSingleLittleEndian(srcSpan.Slice(slotOffset + 4, 4));
            float z = BinaryPrimitives.ReadSingleLittleEndian(srcSpan.Slice(slotOffset + 8, 4));

            int cellI = (int)System.Math.Floor(x * invD);
            int cellJ = (int)System.Math.Floor(y * invD);
            int cellK = (int)System.Math.Floor(z * invD);
            long key = PackCellKey(cellI, cellJ, cellK);

            ref CellAccumulator acc =
                ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(cells, key, out _);
            acc.SumX += x;
            acc.SumY += y;
            acc.SumZ += z;
            acc.Count++;
            if (hasColor)
            {
                acc.SumR += srcSpan[slotOffset + 12];
                acc.SumG += srcSpan[slotOffset + 13];
                acc.SumB += srcSpan[slotOffset + 14];
                acc.SumA += srcSpan[slotOffset + 15];
            }
        }

        int outCount = cells.Count;
        int outStride = PointCloudHeader.PositionStrideBytes
            + (hasColor ? PointCloudHeader.ColorStrideBytes : 0);
        byte[] outBlob = new byte[PointCloudHeader.SizeBytes + (long)outCount * outStride];
        Span<byte> outSpan = outBlob;

        Vector3 bboxMin = new(float.PositiveInfinity);
        Vector3 bboxMax = new(float.NegativeInfinity);
        int writeOffset = PointCloudHeader.SizeBytes;
        foreach (CellAccumulator acc in cells.Values)
        {
            double n = acc.Count;
            float cx = (float)(acc.SumX / n);
            float cy = (float)(acc.SumY / n);
            float cz = (float)(acc.SumZ / n);

            Span<byte> outPoint = outSpan.Slice(writeOffset, outStride);
            BinaryPrimitives.WriteSingleLittleEndian(outPoint[0..4], cx);
            BinaryPrimitives.WriteSingleLittleEndian(outPoint[4..8], cy);
            BinaryPrimitives.WriteSingleLittleEndian(outPoint[8..12], cz);
            if (hasColor)
            {
                outPoint[12] = (byte)(acc.SumR / acc.Count);
                outPoint[13] = (byte)(acc.SumG / acc.Count);
                outPoint[14] = (byte)(acc.SumB / acc.Count);
                outPoint[15] = (byte)(acc.SumA / acc.Count);
            }
            writeOffset += outStride;

            bboxMin = Vector3.Min(bboxMin, new Vector3(cx, cy, cz));
            bboxMax = Vector3.Max(bboxMax, new Vector3(cx, cy, cz));
        }

        PointCloudHeader outHeader = new(
            Version: PointCloudHeader.CurrentVersion,
            Flags: hasColor ? PointCloudFlags.HasColor : PointCloudFlags.None,
            CoordinateFrame: header.CoordinateFrame,
            PointCount: (uint)outCount,
            BboxMin: bboxMin,
            BboxMax: bboxMax,
            Width: 0,
            Height: 0);
        outHeader.Write(outSpan[..PointCloudHeader.SizeBytes]);

        return new ValueTask<ValueRef>(ValueRef.FromPointCloud(outBlob));
    }

    /// <summary>
    /// Packs three signed cell indices into a 64-bit key. 21 bits per axis
    /// → ±1M cells per dimension, more than any realistic scene needs at any
    /// reasonable cell size. Throws on overflow so silent collisions don't
    /// hide a workload mismatch.
    /// </summary>
    private static long PackCellKey(int i, int j, int k)
    {
        const int MaxComponent = 1 << 20;   // ±1,048,576
        if (i <= -MaxComponent || i >= MaxComponent ||
            j <= -MaxComponent || j >= MaxComponent ||
            k <= -MaxComponent || k >= MaxComponent)
        {
            throw new FunctionArgumentException(
                "pc_voxel_downsample",
                $"voxel cell index out of range: ({i}, {j}, {k}). "
                + "Use a larger cell_size or pre-filter points further from the bbox corner.");
        }
        long ui = (long)(uint)(i + MaxComponent) & 0x1F_FFFFL;
        long uj = (long)(uint)(j + MaxComponent) & 0x1F_FFFFL;
        long uk = (long)(uint)(k + MaxComponent) & 0x1F_FFFFL;
        return (ui << 42) | (uj << 21) | uk;
    }

    /// <summary>
    /// Per-cell running sums. Allocated once per occupied cell; no
    /// per-point allocation beyond the dictionary slot.
    /// </summary>
    private struct CellAccumulator
    {
        public double SumX;
        public double SumY;
        public double SumZ;
        public int SumR;
        public int SumG;
        public int SumB;
        public int SumA;
        public int Count;
    }
}
