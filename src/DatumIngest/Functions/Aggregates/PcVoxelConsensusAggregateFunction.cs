using System.Buffers.Binary;
using System.Numerics;

using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Model.Spatial;

namespace Heliosoph.DatumV.Functions.Aggregates;

/// <summary>
/// <c>pc_voxel_consensus_agg(pc PointCloud, cell_size Float32, min_votes Int32) → PointCloud</c>.
/// Combines <c>pc_fuse_agg</c> + <c>pc_voxel_consensus</c> into a single
/// aggregate that voxel-hashes points DURING accumulation. State grows
/// with voxel count (bounded by scene volume), not point count (bounded
/// only by frame count × per-frame points). The big win over
/// <c>pc_voxel_consensus(pc_fuse_agg(_), …)</c>: no intermediate ~500MB
/// raw-point accumulator, and no separate ~30s finalize pass that walks
/// every contributed point.
/// </summary>
/// <remarks>
/// <para>
/// <strong>When to reach for this</strong>: video / multi-frame fusion
/// where you want a voxel-deduplicated point cloud with vote-thresholded
/// consensus. The arithmetic is identical to
/// <c>pc_voxel_consensus(pc_fuse_agg(_), cell_size, min_votes)</c> but
/// the state model is dramatically more efficient — same output, ~10×
/// less peak memory, no end-of-query stall.
/// </para>
/// <para>
/// <strong>Cell convention</strong>: identical to <c>pc_voxel_downsample</c>
/// and <c>pc_voxel_consensus</c> — world-origin-anchored, double-precision
/// floor for boundary stability, 21-bit packed cell index. The same world
/// position lands in the same cell across all callers, so a pipeline can
/// mix this aggregate with the scalar variants without losing dedup.
/// </para>
/// <para>
/// <strong>Color handling</strong>: same as <c>pc_voxel_consensus</c> —
/// output carries color iff every contributing point cloud had color.
/// A single position-only contribution strips color from the output.
/// </para>
/// <para>
/// <strong>Coordinate frame</strong>: the first non-empty contribution
/// with a committed frame pins the output frame; later mismatched commits
/// throw.
/// </para>
/// </remarks>
public sealed class PcVoxelConsensusAggregateFunction : IAggregateFunction, IAggregateFunctionMetadata
{
    /// <inheritdoc cref="IAggregateFunctionMetadata.Name"/>
    public static string Name => "pc_voxel_consensus_agg";

    /// <inheritdoc/>
    string IAggregateFunction.Name => Name;

    /// <inheritdoc/>
    public static FunctionCategory Category => FunctionCategory.Aggregate;

    /// <inheritdoc/>
    public static string Description =>
        "Voxel-hashed aggregate fusion with per-cell vote threshold; bounded by occupied voxel count rather than point count.";

    /// <inheritdoc/>
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("pc",        DataKindMatcher.Exact(DataKind.PointCloud)),
                new ParameterSpec("cell_size", DataKindMatcher.Family(DataKindFamily.FloatFamily)),
                new ParameterSpec("min_votes", DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.PointCloud)),
    ];

    /// <inheritdoc/>
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 3)
        {
            throw new ArgumentException(
                "pc_voxel_consensus_agg() requires exactly 3 arguments: "
                + "(pc PointCloud, cell_size Float32, min_votes Int32).");
        }
        if (argumentKinds[0] != DataKind.PointCloud)
        {
            throw new ArgumentException(
                $"pc_voxel_consensus_agg() argument 0 must be PointCloud; got {argumentKinds[0]}.");
        }
        return DataKind.PointCloud;
    }

    /// <inheritdoc/>
    public ReturnTypeRule ReturnRule { get; } = ReturnTypeRule.Constant(DataKind.PointCloud);

    /// <inheritdoc/>
    public IAggregateAccumulator CreateAccumulator() => new Accumulator();

    /// <summary>
    /// Per-cell sums. Identical layout to <c>PcVoxelConsensusFunction</c>'s
    /// internal CellAccumulator (kept separate to avoid cross-namespace
    /// coupling — they're conceptually the same struct).
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

    private sealed class Accumulator : IAggregateAccumulator
    {
        // The cell dictionary — survives in managed memory across the
        // aggregate's lifetime, bounded by occupied voxel count.
        private readonly Dictionary<long, CellAccumulator> _cells = new();

        // Constants from the first row; validated on subsequent rows.
        private float _cellSize = float.NaN;
        private int _minVotes = -1;

        // First non-Unspecified frame pinned here. Output mesh / cloud
        // inherits this frame.
        private PointCloudCoordinateFrame _committedFrame = PointCloudCoordinateFrame.Unspecified;

        // Color survives iff every non-empty contribution had color. A
        // single position-only contribution strips color.
        private bool _allContributorsHaveColor = true;
        private bool _sawAnyNonEmpty;

        public void Accumulate(ReadOnlySpan<DataValue> arguments, in InvocationFrame frame)
        {
            DataValue pcArg = arguments[0];
            if (pcArg.IsNull) return;

            // Constants — read on first call, validated on subsequent.
            float cellSize = ReadFloatScalar(arguments[1], "cell_size");
            int minVotes = ReadIntScalar(arguments[2], "min_votes");
            if (!(cellSize > 0f) || !float.IsFinite(cellSize))
            {
                throw new FunctionArgumentException(
                    "pc_voxel_consensus_agg",
                    $"cell_size must be positive finite Float32; got {cellSize}.");
            }
            if (minVotes < 1)
            {
                throw new FunctionArgumentException(
                    "pc_voxel_consensus_agg",
                    $"min_votes must be ≥ 1; got {minVotes}.");
            }
            if (float.IsNaN(_cellSize))
            {
                _cellSize = cellSize;
                _minVotes = minVotes;
            }
            else if (_cellSize != cellSize || _minVotes != minVotes)
            {
                throw new FunctionArgumentException(
                    "pc_voxel_consensus_agg",
                    $"cell_size and min_votes must be constant across all rows; "
                    + $"saw ({_cellSize}, {_minVotes}) then ({cellSize}, {minVotes}).");
            }

            // Copy bytes into managed memory immediately so arena mutation
            // can't invalidate spans. Same hazard as the depth+confidence
            // functions — calling AsArraySpan across multiple arena ops is
            // unsafe (see project_arena_thread_safety memo).
            byte[] blob = pcArg.AsByteSpan(frame.Source, frame.SidecarRegistry).ToArray();
            PointCloudHeader header = PointCloudHeader.Read(blob);

            // Slice A: position + optional color only. Reject normals /
            // intensity for now — they'd need per-attribute accumulation.
            PointCloudFlags unsupported = header.Flags & ~PointCloudHeader.SupportedFlags;
            if (unsupported != PointCloudFlags.None)
            {
                throw new FunctionArgumentException(
                    "pc_voxel_consensus_agg",
                    $"PointCloud carries unsupported attributes ({unsupported}); "
                    + "pc_voxel_consensus_agg currently handles position + optional color only.");
            }

            if (header.PointCount == 0) return;

            // Coordinate-frame commit. Mirror pc_fuse_agg's behavior:
            // first non-Unspecified frame wins, subsequent mismatches throw.
            if (header.CoordinateFrame != PointCloudCoordinateFrame.Unspecified)
            {
                if (_committedFrame == PointCloudCoordinateFrame.Unspecified)
                {
                    _committedFrame = header.CoordinateFrame;
                }
                else if (_committedFrame != header.CoordinateFrame)
                {
                    throw new FunctionArgumentException(
                        "pc_voxel_consensus_agg",
                        $"PointCloud coordinate frames disagree: existing "
                        + $"{_committedFrame}, incoming {header.CoordinateFrame}. "
                        + "Transform contributions into a common frame before aggregating.");
                }
            }

            if (!header.HasColor) _allContributorsHaveColor = false;
            _sawAnyNonEmpty = true;

            // Hash each point into its voxel cell.
            int srcStride = header.PointStrideBytes;
            int srcBase = PointCloudHeader.SizeBytes;
            bool hasColor = header.HasColor;
            double invD = 1.0 / cellSize;
            ReadOnlySpan<byte> srcSpan = blob;

            for (uint i = 0; i < header.PointCount; i++)
            {
                int slotOffset = srcBase + (int)i * srcStride;
                float x = BinaryPrimitives.ReadSingleLittleEndian(srcSpan.Slice(slotOffset + 0, 4));
                float y = BinaryPrimitives.ReadSingleLittleEndian(srcSpan.Slice(slotOffset + 4, 4));
                float z = BinaryPrimitives.ReadSingleLittleEndian(srcSpan.Slice(slotOffset + 8, 4));

                int cellI = (int)System.Math.Floor(x * invD);
                int cellJ = (int)System.Math.Floor(y * invD);
                int cellK = (int)System.Math.Floor(z * invD);
                long key = PackCellKey(cellI, cellJ, cellK);

                ref CellAccumulator acc =
                    ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(_cells, key, out _);
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
        }

        public ValueTask MergeAsync(IAggregateAccumulator other, InvocationFrame frame)
        {
            Accumulator o = (Accumulator)other;

            // Resolve constants — first non-NaN wins; mismatches throw.
            if (float.IsNaN(_cellSize) && !float.IsNaN(o._cellSize))
            {
                _cellSize = o._cellSize;
                _minVotes = o._minVotes;
            }
            else if (!float.IsNaN(_cellSize) && !float.IsNaN(o._cellSize))
            {
                if (_cellSize != o._cellSize || _minVotes != o._minVotes)
                {
                    throw new FunctionArgumentException(
                        "pc_voxel_consensus_agg",
                        $"cell_size/min_votes disagree across merge: "
                        + $"({_cellSize}, {_minVotes}) vs ({o._cellSize}, {o._minVotes}).");
                }
            }

            // Resolve coord frame.
            if (o._committedFrame != PointCloudCoordinateFrame.Unspecified)
            {
                if (_committedFrame == PointCloudCoordinateFrame.Unspecified)
                {
                    _committedFrame = o._committedFrame;
                }
                else if (_committedFrame != o._committedFrame)
                {
                    throw new FunctionArgumentException(
                        "pc_voxel_consensus_agg",
                        $"PointCloud coordinate frames disagree across merge: "
                        + $"{_committedFrame} vs {o._committedFrame}.");
                }
            }

            if (!o._allContributorsHaveColor) _allContributorsHaveColor = false;
            if (o._sawAnyNonEmpty) _sawAnyNonEmpty = true;

            // Merge cell sums.
            foreach (KeyValuePair<long, CellAccumulator> kv in o._cells)
            {
                ref CellAccumulator existing =
                    ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(_cells, kv.Key, out _);
                existing.SumX += kv.Value.SumX;
                existing.SumY += kv.Value.SumY;
                existing.SumZ += kv.Value.SumZ;
                existing.SumR += kv.Value.SumR;
                existing.SumG += kv.Value.SumG;
                existing.SumB += kv.Value.SumB;
                existing.SumA += kv.Value.SumA;
                existing.Count += kv.Value.Count;
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask<DataValue> ResultAsync(InvocationFrame frame)
        {
            // Empty aggregation → empty cloud.
            if (!_sawAnyNonEmpty || _cells.Count == 0)
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
                return new ValueTask<DataValue>(DataValue.FromPointCloud(empty, frame.Target));
            }

            // Count survivors so we can pre-size the output blob exactly.
            int survivorCount = 0;
            foreach (CellAccumulator acc in _cells.Values)
            {
                if (acc.Count >= _minVotes) survivorCount++;
            }

            bool outHasColor = _allContributorsHaveColor;
            int outStride = PointCloudHeader.PositionStrideBytes
                + (outHasColor ? PointCloudHeader.ColorStrideBytes : 0);
            byte[] outBlob = new byte[PointCloudHeader.SizeBytes + (long)survivorCount * outStride];
            Span<byte> outSpan = outBlob;

            Vector3 bboxMin = new(float.PositiveInfinity);
            Vector3 bboxMax = new(float.NegativeInfinity);
            int writeOffset = PointCloudHeader.SizeBytes;
            foreach (CellAccumulator acc in _cells.Values)
            {
                if (acc.Count < _minVotes) continue;

                double n = acc.Count;
                float cx = (float)(acc.SumX / n);
                float cy = (float)(acc.SumY / n);
                float cz = (float)(acc.SumZ / n);

                Span<byte> outPoint = outSpan.Slice(writeOffset, outStride);
                BinaryPrimitives.WriteSingleLittleEndian(outPoint[0..4], cx);
                BinaryPrimitives.WriteSingleLittleEndian(outPoint[4..8], cy);
                BinaryPrimitives.WriteSingleLittleEndian(outPoint[8..12], cz);
                if (outHasColor)
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
                Flags: outHasColor ? PointCloudFlags.HasColor : PointCloudFlags.None,
                CoordinateFrame: _committedFrame,
                PointCount: (uint)survivorCount,
                BboxMin: bboxMin,
                BboxMax: bboxMax,
                Width: 0,
                Height: 0);
            outHeader.Write(outSpan[..PointCloudHeader.SizeBytes]);

            return new ValueTask<DataValue>(DataValue.FromPointCloud(outBlob, frame.Target));
        }

        public void Reset()
        {
            _cells.Clear();
            _cellSize = float.NaN;
            _minVotes = -1;
            _committedFrame = PointCloudCoordinateFrame.Unspecified;
            _allContributorsHaveColor = true;
            _sawAnyNonEmpty = false;
        }

        // ─────────── Same cell-key packing as pc_voxel_consensus ───────────

        private static long PackCellKey(int i, int j, int k)
        {
            const int MaxComponent = 1 << 20;
            if (i <= -MaxComponent || i >= MaxComponent ||
                j <= -MaxComponent || j >= MaxComponent ||
                k <= -MaxComponent || k >= MaxComponent)
            {
                throw new FunctionArgumentException(
                    "pc_voxel_consensus_agg",
                    $"voxel cell index out of range: ({i}, {j}, {k}). "
                    + "Increase cell_size or constrain the scene.");
            }
            long ui = (long)(uint)(i + MaxComponent) & 0x1F_FFFFL;
            long uj = (long)(uint)(j + MaxComponent) & 0x1F_FFFFL;
            long uk = (long)(uint)(k + MaxComponent) & 0x1F_FFFFL;
            return (ui << 42) | (uj << 21) | uk;
        }

        // ─────────── Helpers ───────────

        private static float ReadFloatScalar(DataValue value, string name)
        {
            if (value.TryToFloat(out float result)) return result;
            throw new FunctionArgumentException(
                "pc_voxel_consensus_agg",
                $"{name} could not be widened to Float32 (kind {value.Kind}).");
        }

        private static int ReadIntScalar(DataValue value, string name)
        {
            if (value.TryToInt32(out int result)) return result;
            throw new FunctionArgumentException(
                "pc_voxel_consensus_agg",
                $"{name} could not be widened to Int32 (kind {value.Kind}).");
        }
    }
}
