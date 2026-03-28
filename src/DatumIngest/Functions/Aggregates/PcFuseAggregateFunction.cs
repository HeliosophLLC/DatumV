using System.Buffers.Binary;
using System.Numerics;

using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Model.Spatial;

namespace DatumIngest.Functions.Aggregates;

/// <summary>
/// <c>pc_fuse_agg(pc)</c> — aggregate variant of <c>pc_fuse</c>. Folds every
/// non-null PointCloud in a group into a single concatenated cloud, emitted
/// once per group at finalize time. Same merge semantics as the scalar
/// <c>pc_fuse</c> (concat positions+colors, union bbox, all-or-none color,
/// coord-frame agreement) but holds running state in *managed memory*
/// instead of the per-row arena.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists.</strong> A SCAN fold over PointClouds materializes
/// every intermediate <c>world_t</c> row in the value arena, which is capped
/// at ~2 GB (int32-indexed). For "give me the final fused world" workloads
/// — the static-reconstruction A/C demos — the per-tick history isn't
/// wanted, only the final cloud. This aggregate keeps the running state as
/// a managed <c>List&lt;byte[]&gt;</c> of per-row contributions, then
/// allocates the final blob exactly once at <see cref="IAggregateAccumulator.ResultAsync"/>.
/// Arena pressure drops from O(N²) intermediate rows to O(final_size).
/// </para>
/// <para>
/// <strong>Color handling.</strong> Output carries color iff every
/// contributing cloud carries color. A single position-only contribution
/// strips color from the merged output — same rule as <c>pc_fuse</c>.
/// </para>
/// <para>
/// <strong>Coordinate frames.</strong> The first non-empty contribution
/// with a committed (non-Unspecified) frame pins the output frame; subsequent
/// committed frames must agree, otherwise throws. Mirrors <c>pc_fuse</c>.
/// </para>
/// </remarks>
public sealed class PcFuseAggregateFunction : IAggregateFunction
{
    /// <inheritdoc/>
    public string Name => "pc_fuse_agg";

    /// <inheritdoc/>
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("pc_fuse_agg() requires exactly one argument.");
        }
        if (argumentKinds[0] != DataKind.PointCloud)
        {
            throw new ArgumentException(
                $"pc_fuse_agg() argument must be PointCloud, got {argumentKinds[0]}.");
        }
        return DataKind.PointCloud;
    }

    /// <inheritdoc/>
    public ReturnTypeRule ReturnRule { get; } = ReturnTypeRule.Constant(DataKind.PointCloud);

    /// <inheritdoc/>
    public IAggregateAccumulator CreateAccumulator() => new Accumulator();

    /// <summary>
    /// Aggregate state. Holds each non-null incoming PointCloud blob by
    /// managed-array reference (a <c>byte[]</c> copy taken from the source
    /// arena at <see cref="Accumulate"/> time). The final cloud is built once
    /// at <see cref="ResultAsync"/>; until then, no arena bytes are written.
    /// </summary>
    private sealed class Accumulator : IAggregateAccumulator
    {
        // Each entry is a self-contained PointCloud blob (header + payload)
        // owned by managed memory. Disconnected from the source arena.
        private readonly List<byte[]> _blobs = new();

        // Running aggregate state — updated incrementally each Accumulate so
        // ResultAsync can size the output blob without rewalking every blob.
        private long _totalPoints;
        private Vector3 _bboxMin = new(float.PositiveInfinity);
        private Vector3 _bboxMax = new(float.NegativeInfinity);
        private PointCloudCoordinateFrame _committedFrame = PointCloudCoordinateFrame.Unspecified;
        private bool _allContributorsHaveColor = true;
        private bool _sawAnyNonEmpty;

        public void Accumulate(ReadOnlySpan<DataValue> arguments, in InvocationFrame frame)
        {
            DataValue arg = arguments[0];
            if (arg.IsNull) return;

            // Copy into managed memory — disconnects from the source arena so
            // subsequent arena reuse can't corrupt our accumulator state.
            byte[] blob = arg.AsByteSpan(frame.Source, frame.SidecarRegistry).ToArray();
            PointCloudHeader header = PointCloudHeader.Read(blob);

            // Slice A: position + optional color only. Reject normals /
            // intensity until pc_fuse_agg grows to handle them.
            PointCloudFlags unsupported = header.Flags & ~PointCloudHeader.SupportedFlags;
            if (unsupported != PointCloudFlags.None)
            {
                throw new FunctionArgumentException(
                    "pc_fuse_agg",
                    $"PointCloud carries unsupported attributes ({unsupported}); "
                    + "pc_fuse_agg currently handles position + optional color only.");
            }

            _blobs.Add(blob);

            if (header.PointCount == 0)
            {
                // Empty contributions skip color/frame voting (they have no
                // committed colors or positions). Matches pc_fuse semantics
                // where pc_empty() doesn't strip color from a real cloud.
                return;
            }

            _totalPoints += header.PointCount;
            _bboxMin = Vector3.Min(_bboxMin, header.BboxMin);
            _bboxMax = Vector3.Max(_bboxMax, header.BboxMax);

            if (!header.HasColor) _allContributorsHaveColor = false;
            _sawAnyNonEmpty = true;

            if (header.CoordinateFrame != PointCloudCoordinateFrame.Unspecified)
            {
                if (_committedFrame == PointCloudCoordinateFrame.Unspecified)
                {
                    _committedFrame = header.CoordinateFrame;
                }
                else if (_committedFrame != header.CoordinateFrame)
                {
                    throw new FunctionArgumentException(
                        "pc_fuse_agg",
                        $"PointCloud coordinate frames disagree: existing "
                        + $"{_committedFrame}, incoming {header.CoordinateFrame}. "
                        + "Transform contributions into a common frame before aggregating.");
                }
            }
        }

        public ValueTask MergeAsync(IAggregateAccumulator other, InvocationFrame frame)
        {
            Accumulator o = (Accumulator)other;

            // Merge incoming blobs and running totals. Coord-frame agreement
            // must hold across the merged side too — if either side committed
            // a frame, the other side's committed frame must match.
            if (o._committedFrame != PointCloudCoordinateFrame.Unspecified)
            {
                if (_committedFrame == PointCloudCoordinateFrame.Unspecified)
                {
                    _committedFrame = o._committedFrame;
                }
                else if (_committedFrame != o._committedFrame)
                {
                    throw new FunctionArgumentException(
                        "pc_fuse_agg",
                        $"PointCloud coordinate frames disagree across merge: "
                        + $"{_committedFrame} vs {o._committedFrame}.");
                }
            }

            _blobs.AddRange(o._blobs);
            _totalPoints += o._totalPoints;
            if (o._sawAnyNonEmpty)
            {
                _bboxMin = Vector3.Min(_bboxMin, o._bboxMin);
                _bboxMax = Vector3.Max(_bboxMax, o._bboxMax);
                _sawAnyNonEmpty = true;
            }
            if (!o._allContributorsHaveColor) _allContributorsHaveColor = false;

            return ValueTask.CompletedTask;
        }

        public ValueTask<DataValue> ResultAsync(InvocationFrame frame)
        {
            // Empty aggregation → zero-point cloud, same shape as pc_empty().
            // Matches what a SCAN folding pc_empty() INIT would produce
            // before any contributions arrive.
            if (_totalPoints == 0)
            {
                byte[] empty = new byte[PointCloudHeader.SizeBytes];
                PointCloudHeader emptyHeader = new(
                    Version: PointCloudHeader.CurrentVersion,
                    Flags: PointCloudFlags.None,
                    CoordinateFrame: PointCloudCoordinateFrame.Unspecified,
                    PointCount: 0,
                    BboxMin: Vector3.Zero,
                    BboxMax: Vector3.Zero,
                    Width: 0,
                    Height: 0);
                emptyHeader.Write(empty);
                return new ValueTask<DataValue>(
                    DataValue.FromPointCloud(empty, frame.Target));
            }

            if (_totalPoints > uint.MaxValue)
            {
                throw new FunctionArgumentException(
                    "pc_fuse_agg",
                    $"fused point count {_totalPoints} exceeds the uint32 limit.");
            }

            bool outHasColor = _allContributorsHaveColor && _sawAnyNonEmpty;
            PointCloudFlags outFlags = outHasColor ? PointCloudFlags.HasColor : PointCloudFlags.None;
            int outStride = PointCloudHeader.PositionStrideBytes
                + (outHasColor ? PointCloudHeader.ColorStrideBytes : 0);

            byte[] outBlob = new byte[PointCloudHeader.SizeBytes + _totalPoints * outStride];
            Span<byte> outSpan = outBlob;

            PointCloudHeader outHeader = new(
                Version: PointCloudHeader.CurrentVersion,
                Flags: outFlags,
                CoordinateFrame: _committedFrame,
                PointCount: (uint)_totalPoints,
                BboxMin: _bboxMin,
                BboxMax: _bboxMax,
                Width: 0,    // fused output is always unorganized
                Height: 0);
            outHeader.Write(outSpan[..PointCloudHeader.SizeBytes]);

            int writeOffset = PointCloudHeader.SizeBytes;
            foreach (byte[] blob in _blobs)
            {
                PointCloudHeader h = PointCloudHeader.Read(blob);
                writeOffset = CopyPoints(blob, h, outSpan, writeOffset, outHasColor);
            }

            return new ValueTask<DataValue>(
                DataValue.FromPointCloud(outBlob, frame.Target));
        }

        public void Reset()
        {
            _blobs.Clear();
            _totalPoints = 0;
            _bboxMin = new Vector3(float.PositiveInfinity);
            _bboxMax = new Vector3(float.NegativeInfinity);
            _committedFrame = PointCloudCoordinateFrame.Unspecified;
            _allContributorsHaveColor = true;
            _sawAnyNonEmpty = false;
        }

        /// <summary>
        /// Mirrors the byte-copy logic in <c>pc_fuse</c>'s scalar implementation
        /// (single source → block-copy when stride matches; stripping-color
        /// path otherwise). Kept inline rather than reusing the scalar's
        /// private helper because <c>PcFuseFunction.CopyPoints</c> isn't
        /// visible from outside its class — duplicating ~30 lines beats
        /// exposing internal helpers as cross-namespace API surface.
        /// </summary>
        private static int CopyPoints(
            byte[] sourceBlob,
            PointCloudHeader sourceHeader,
            Span<byte> dstSpan,
            int dstOffset,
            bool outHasColor)
        {
            if (sourceHeader.PointCount == 0) return dstOffset;

            int srcStride = sourceHeader.PointStrideBytes;
            int srcBase = PointCloudHeader.SizeBytes;
            int outStride = PointCloudHeader.PositionStrideBytes
                + (outHasColor ? PointCloudHeader.ColorStrideBytes : 0);
            ReadOnlySpan<byte> srcSpan = sourceBlob;

            if (srcStride == outStride)
            {
                int payloadBytes = checked((int)sourceHeader.PointCount * srcStride);
                srcSpan.Slice(srcBase, payloadBytes)
                    .CopyTo(dstSpan.Slice(dstOffset, payloadBytes));
                return dstOffset + payloadBytes;
            }

            // Mismatched stride: source has color, output doesn't (we're
            // stripping the trailing 4 color bytes per point). Per-point copy
            // of just the position prefix.
            for (uint i = 0; i < sourceHeader.PointCount; i++)
            {
                ReadOnlySpan<byte> srcPoint = srcSpan.Slice(
                    srcBase + (int)i * srcStride,
                    PointCloudHeader.PositionStrideBytes);
                srcPoint.CopyTo(dstSpan.Slice(dstOffset, PointCloudHeader.PositionStrideBytes));
                dstOffset += outStride;
            }
            return dstOffset;
        }
    }
}
