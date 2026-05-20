using System.Buffers.Binary;
using System.Numerics;

using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Model.Spatial;

namespace Heliosoph.DatumV.Functions.Scalar.Spatial;

/// <summary>
/// <c>pc_voxel_consensus(pc PointCloud, cell_size Float32, min_votes Int32) → PointCloud</c>.
/// Like <c>pc_voxel_downsample</c>, but only emits voxels with at least
/// <paramref>min_votes</paramref> contributing points. The natural anti-ghost
/// primitive for multi-frame fusion: real surfaces accumulate votes from every
/// frame that saw them; ghost geometry (single-frame noise, gross pose outliers,
/// depth-estimation failure pixels) has only one or two votes and gets culled.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Anti-ghost mechanism.</strong> When fusing N frames of an indoor
/// scene with imperfect poses + depth, each real surface gets observed many
/// times — contributions cluster within ~one cell of the true position. A
/// 2cm cell with <c>min_votes=3</c> on a 30-frame sequence keeps voxels seen
/// by ≥3 frames, dropping the floating debris that depth models inevitably
/// emit at object edges and far ranges.
/// </para>
/// <para>
/// <strong>Idempotency caveat.</strong> Unlike <c>pc_voxel_downsample</c>,
/// this is NOT idempotent — a second application with the same
/// <paramref>min_votes</paramref> would drop everything (each voxel now has
/// exactly one contributing point: the centroid emitted by the first pass).
/// Use it as the final cleanup step, not inside the fold.
/// </para>
/// <para>
/// <strong>Cell convention.</strong> Same as <c>pc_voxel_downsample</c> —
/// world-origin anchored, double-precision floor for boundary stability,
/// 21-bit packed cell index.
/// </para>
/// </remarks>
public sealed class PcVoxelConsensusFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "pc_voxel_consensus";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Spatial;

    /// <inheritdoc />
    public static string Description =>
        "Like pc_voxel_downsample, but only emits voxels with at least min_votes "
        + "contributing points. Drops single-frame ghosts and depth-model noise from "
        + "multi-frame fusion — real surfaces survive because every frame that saw "
        + "them votes for the same voxel; ghosts get one vote and lose. Not "
        + "idempotent — apply only as a final cleanup step, not inside a fold.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("pc", DataKindMatcher.Exact(DataKind.PointCloud)),
                new ParameterSpec("cell_size", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("min_votes", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.PointCloud)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<PcVoxelConsensusFunction>(argumentKinds);

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

        if (!args[1].TryToFloat(out float cellSize) || !(cellSize > 0f) || !float.IsFinite(cellSize))
        {
            throw new FunctionArgumentException(
                Name,
                $"cell_size must be a positive finite Float32; got {args[1].Kind}.");
        }
        if (!args[2].TryToInt32(out int minVotes) || minVotes < 1)
        {
            throw new FunctionArgumentException(
                Name,
                $"min_votes must be a positive Int32 (≥ 1); got {args[2].Kind}.");
        }

        byte[] srcBlob = args[0].AsPointCloud();
        PointCloudHeader header = PointCloudHeader.Read(srcBlob);
        int srcCount = checked((int)header.PointCount);
        int stride = header.PointStrideBytes;
        int srcBase = PointCloudHeader.SizeBytes;
        bool hasColor = header.HasColor;
        ReadOnlySpan<byte> srcSpan = srcBlob;

        // Empty input → empty output.
        if (srcCount == 0)
        {
            byte[] empty = new byte[PointCloudHeader.SizeBytes];
            header.Write(empty);
            return new ValueTask<ValueRef>(ValueRef.FromPointCloud(empty));
        }

        // World-origin-anchored cell index, double-precision floor for boundary
        // stability. Same convention as pc_voxel_downsample so a cloud passed
        // through both functions partitions identically.
        double invD = 1.0 / cellSize;

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

        // First pass to count survivors: cells with vote count ≥ min_votes.
        // Pre-sizing the output blob saves the resize-and-trim that two-pass
        // counting avoids.
        int survivorCount = 0;
        foreach (CellAccumulator acc in cells.Values)
        {
            if (acc.Count >= minVotes) survivorCount++;
        }

        int outStride = PointCloudHeader.PositionStrideBytes
            + (hasColor ? PointCloudHeader.ColorStrideBytes : 0);
        byte[] outBlob = new byte[PointCloudHeader.SizeBytes + (long)survivorCount * outStride];
        Span<byte> outSpan = outBlob;

        Vector3 bboxMin = new(float.PositiveInfinity);
        Vector3 bboxMax = new(float.NegativeInfinity);
        int writeOffset = PointCloudHeader.SizeBytes;
        foreach (CellAccumulator acc in cells.Values)
        {
            if (acc.Count < minVotes) continue;

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

        if (survivorCount == 0)
        {
            bboxMin = Vector3.Zero;
            bboxMax = Vector3.Zero;
        }

        PointCloudHeader outHeader = new(
            Version: PointCloudHeader.CurrentVersion,
            Flags: hasColor ? PointCloudFlags.HasColor : PointCloudFlags.None,
            CoordinateFrame: header.CoordinateFrame,
            PointCount: (uint)survivorCount,
            BboxMin: bboxMin,
            BboxMax: bboxMax,
            Width: 0,
            Height: 0);
        outHeader.Write(outSpan[..PointCloudHeader.SizeBytes]);

        return new ValueTask<ValueRef>(ValueRef.FromPointCloud(outBlob));
    }

    /// <summary>
    /// Mirror of <c>PcVoxelDownsampleFunction.PackCellKey</c> — same packing
    /// scheme so a cloud passed through both functions partitions into the
    /// same cell IDs. Duplicated rather than shared to keep each scalar
    /// self-contained at the file level.
    /// </summary>
    private static long PackCellKey(int i, int j, int k)
    {
        const int MaxComponent = 1 << 20;
        if (i <= -MaxComponent || i >= MaxComponent ||
            j <= -MaxComponent || j >= MaxComponent ||
            k <= -MaxComponent || k >= MaxComponent)
        {
            throw new FunctionArgumentException(
                "pc_voxel_consensus",
                $"voxel cell index out of range: ({i}, {j}, {k}). "
                + "Use a larger cell_size or pre-filter points further from the bbox corner.");
        }
        long ui = (long)(uint)(i + MaxComponent) & 0x1F_FFFFL;
        long uj = (long)(uint)(j + MaxComponent) & 0x1F_FFFFL;
        long uk = (long)(uint)(k + MaxComponent) & 0x1F_FFFFL;
        return (ui << 42) | (uj << 21) | uk;
    }

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
