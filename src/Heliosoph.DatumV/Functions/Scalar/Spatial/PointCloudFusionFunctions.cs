using System.Buffers.Binary;
using System.Numerics;

using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Model.Spatial;

namespace Heliosoph.DatumV.Functions.Scalar.Spatial;

/// <summary>
/// <c>pc_fuse(a PointCloud, b PointCloud) → PointCloud</c>. Concatenates two
/// PointClouds into a single unorganized cloud carrying the union of points.
/// Bounding boxes union; coordinate frame is the non-Unspecified one when only
/// one side is committed, otherwise must agree. No deduplication, no
/// voxel-grid downsample — that's a follow-up.
/// </summary>
/// <remarks>
/// <para>
/// Designed as the fold step for SCAN-over-frames pipelines: SCAN accumulator
/// = pc_fuse(world, point_cloud_from_image(frame, depth)). The accumulator
/// grows linearly in point count; voxel-grid downsample is the natural
/// follow-up when bounded memory matters.
/// </para>
/// <para>
/// Color attribute handling: the output carries color iff both inputs carry
/// color. Mixed (one with, one without) drops color from the output, since
/// per-cloud invented colors would visually misrepresent the source. Either
/// input being empty (<c>PointCount = 0</c>) is treated as a no-op for that
/// side — the result inherits the non-empty side's flags and frame.
/// </para>
/// </remarks>
public sealed class PcFuseFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "pc_fuse";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Spatial;

    /// <inheritdoc />
    public static string Description =>
        "Concatenates two PointClouds into one unorganized cloud (a.points ++ b.points), "
        + "unioning bounding boxes. Designed for SCAN folds where each iteration fuses a "
        + "new depth-derived cloud into a growing accumulator. Output carries color iff "
        + "both inputs do; coordinate frames must agree (or one must be Unspecified).";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("a", DataKindMatcher.Exact(DataKind.PointCloud)),
                new ParameterSpec("b", DataKindMatcher.Exact(DataKind.PointCloud)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.PointCloud)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<PcFuseFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef aArg = arguments.Span[0];
        ValueRef bArg = arguments.Span[1];

        // NULL propagation matches every other scalar — either NULL input
        // collapses the result to NULL rather than acting as an identity.
        // SCAN folds seed with pc_empty(), not NULL, so this stays clean.
        if (aArg.IsNull || bArg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.PointCloud));
        }

        byte[] aBlob = aArg.AsPointCloud();
        byte[] bBlob = bArg.AsPointCloud();
        PointCloudHeader aHeader = PointCloudHeader.Read(aBlob);
        PointCloudHeader bHeader = PointCloudHeader.Read(bBlob);

        // Slice A only supports position + optional color. Reject anything
        // exotic so we don't silently drop normals/intensity at the fuse
        // step — those land as later work.
        RejectUnsupportedFlags(aHeader, "a");
        RejectUnsupportedFlags(bHeader, "b");

        long totalPoints = (long)aHeader.PointCount + bHeader.PointCount;
        if (totalPoints > uint.MaxValue)
        {
            throw new FunctionArgumentException(
                Name,
                $"fused point count {totalPoints} exceeds the uint32 limit "
                + $"({aHeader.PointCount} + {bHeader.PointCount}).");
        }

        // Output carries color iff both inputs do. Mixed = drop color rather
        // than invent it. Empty inputs (PointCount=0) don't get a vote — the
        // non-empty side's flags win, so an empty seed never strips color.
        bool aContributesColor = aHeader.HasColor || aHeader.PointCount == 0;
        bool bContributesColor = bHeader.HasColor || bHeader.PointCount == 0;
        bool outHasColor = aContributesColor && bContributesColor
            && (aHeader.HasColor || bHeader.HasColor);

        PointCloudCoordinateFrame outFrame = ResolveCoordinateFrame(aHeader, bHeader);

        PointCloudFlags outFlags = outHasColor ? PointCloudFlags.HasColor : PointCloudFlags.None;
        int outStride = PointCloudHeader.PositionStrideBytes
            + (outHasColor ? PointCloudHeader.ColorStrideBytes : 0);

        Vector3 bboxMin = UnionMin(aHeader, bHeader);
        Vector3 bboxMax = UnionMax(aHeader, bHeader);
        // Degenerate fuse (both empty) → leave bbox at origin; the IsOrganized
        // gate already rejects it as unorganized so consumers know not to
        // trust the corners.
        if (totalPoints == 0)
        {
            bboxMin = Vector3.Zero;
            bboxMax = Vector3.Zero;
        }

        byte[] outBlob = new byte[PointCloudHeader.SizeBytes + totalPoints * outStride];
        Span<byte> outSpan = outBlob;

        PointCloudHeader outHeader = new(
            Version: PointCloudHeader.CurrentVersion,
            Flags: outFlags,
            CoordinateFrame: outFrame,
            PointCount: (uint)totalPoints,
            BboxMin: bboxMin,
            BboxMax: bboxMax,
            Width: 0,    // fused cloud has no grid structure
            Height: 0);
        outHeader.Write(outSpan[..PointCloudHeader.SizeBytes]);

        int writeOffset = PointCloudHeader.SizeBytes;
        writeOffset = CopyPoints(aBlob, aHeader, outSpan, writeOffset, outHasColor);
        _ = CopyPoints(bBlob, bHeader, outSpan, writeOffset, outHasColor);

        return new ValueTask<ValueRef>(ValueRef.FromPointCloud(outBlob));
    }

    private void RejectUnsupportedFlags(PointCloudHeader header, string paramName)
    {
        PointCloudFlags unsupported = header.Flags & ~PointCloudHeader.SupportedFlags;
        if (unsupported != PointCloudFlags.None)
        {
            throw new FunctionArgumentException(
                Name,
                $"PointCloud '{paramName}' carries unsupported attributes ({unsupported}); "
                + "pc_fuse currently handles position + optional color only.");
        }
    }

    private PointCloudCoordinateFrame ResolveCoordinateFrame(
        PointCloudHeader a, PointCloudHeader b)
    {
        // Empty side has no committed frame regardless of what its header
        // says — pc_empty() emits Unspecified, but a hand-built empty cloud
        // could carry anything; either way it contributes no real points so
        // its frame doesn't matter.
        bool aCommits = a.PointCount > 0 && a.CoordinateFrame != PointCloudCoordinateFrame.Unspecified;
        bool bCommits = b.PointCount > 0 && b.CoordinateFrame != PointCloudCoordinateFrame.Unspecified;

        if (aCommits && bCommits)
        {
            if (a.CoordinateFrame != b.CoordinateFrame)
            {
                throw new FunctionArgumentException(
                    Name,
                    $"PointCloud coordinate frames disagree: a={a.CoordinateFrame}, "
                    + $"b={b.CoordinateFrame}. Transform one side into the other's frame "
                    + "before fusing (no in-engine frame conversion yet).");
            }
            return a.CoordinateFrame;
        }
        if (aCommits) return a.CoordinateFrame;
        if (bCommits) return b.CoordinateFrame;
        return PointCloudCoordinateFrame.Unspecified;
    }

    private static Vector3 UnionMin(PointCloudHeader a, PointCloudHeader b)
    {
        if (a.PointCount == 0) return b.BboxMin;
        if (b.PointCount == 0) return a.BboxMin;
        return Vector3.Min(a.BboxMin, b.BboxMin);
    }

    private static Vector3 UnionMax(PointCloudHeader a, PointCloudHeader b)
    {
        if (a.PointCount == 0) return b.BboxMax;
        if (b.PointCount == 0) return a.BboxMax;
        return Vector3.Max(a.BboxMax, b.BboxMax);
    }

    /// <summary>
    /// Copies the point payload of <paramref name="sourceBlob"/> into
    /// <paramref name="dstSpan"/> starting at <paramref name="dstOffset"/>,
    /// matching the output stride. Returns the new write offset.
    /// </summary>
    /// <remarks>
    /// Three same-stride cases hit the fast path (one memcpy per point or
    /// even per cloud); the asymmetric "source has color, output doesn't"
    /// case strips the trailing 4 bytes per point.
    /// </remarks>
    private static int CopyPoints(
        byte[] sourceBlob,
        PointCloudHeader sourceHeader,
        Span<byte> dstSpan,
        int dstOffset,
        bool outHasColor)
    {
        if (sourceHeader.PointCount == 0)
        {
            return dstOffset;
        }

        int srcStride = sourceHeader.PointStrideBytes;
        int srcBase = PointCloudHeader.SizeBytes;
        int outStride = PointCloudHeader.PositionStrideBytes
            + (outHasColor ? PointCloudHeader.ColorStrideBytes : 0);
        ReadOnlySpan<byte> srcSpan = sourceBlob;

        if (srcStride == outStride)
        {
            // Same stride → one contiguous block copy. Covers both
            // (position-only → position-only) and (color → color).
            int payloadBytes = checked((int)sourceHeader.PointCount * srcStride);
            srcSpan.Slice(srcBase, payloadBytes)
                .CopyTo(dstSpan.Slice(dstOffset, payloadBytes));
            return dstOffset + payloadBytes;
        }

        // Mismatched stride → per-point copy. With Slice A's two supported
        // flag sets (position vs position+color), this only fires when one
        // input has color and the other doesn't, and we're stripping the
        // color side down to position-only.
        for (uint i = 0; i < sourceHeader.PointCount; i++)
        {
            ReadOnlySpan<byte> srcPoint = srcSpan.Slice(
                srcBase + (int)i * srcStride, PointCloudHeader.PositionStrideBytes);
            srcPoint.CopyTo(dstSpan.Slice(dstOffset, PointCloudHeader.PositionStrideBytes));
            dstOffset += outStride;
        }
        return dstOffset;
    }
}
