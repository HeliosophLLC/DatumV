using Heliosoph.DatumV.Functions.Image;
using Heliosoph.DatumV.Functions.Scalar.Spatial;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Model.Spatial;

using SkiaSharp;

namespace Heliosoph.DatumV.Functions.Aggregates;

/// <summary>
/// <c>pc_fuse_render_agg(pc PointCloud, view_pose Float32[], width Int,
/// height Int, fov_deg Float32 [, point_size Int]) → Array&lt;Image&gt;</c>.
/// Progressive-fusion renderer: every accumulated row renders the union of
/// all clouds seen so far from that row's <c>view_pose</c>, producing one
/// frame per row — a movie of the world building out. Feed the result to
/// <c>frames_to_gif</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why an aggregate.</strong> The running-fusion alternative
/// (<c>pc_fuse_agg(...) OVER (ORDER BY frame_index)</c> then a scalar render)
/// materializes every intermediate world as an arena blob — O(N²) bytes for
/// N frames. This aggregate keeps one managed blob list and emits only the
/// rendered frames, so peak state is O(world + frames) and the arena never
/// sees an intermediate world.
/// </para>
/// <para>
/// <strong>Ordering.</strong> Frames are rendered in accumulation order.
/// Use the intra-aggregate sort to make that explicit:
/// <c>pc_fuse_render_agg(pc, pose, w, h, fov ORDER BY frame_index)</c>.
/// </para>
/// <para>
/// <strong>Per-row camera.</strong> <c>view_pose</c> is evaluated per row
/// (camera-to-world, 4×4 row-major — same convention as <c>pc_render</c> and
/// the <c>pose_*</c> family), so the camera path is an ordinary SQL
/// expression: pass the row's cumulative pose for a chase-cam that flies the
/// recovered trajectory, a constant pose for a fixed god-view, or any
/// composition in between.
/// </para>
/// <para>
/// <strong>Cost.</strong> Each row re-projects every point accumulated so
/// far — O(N²/2) point projections across the group. Feed
/// <c>pc_voxel_downsample</c>ed clouds to keep per-frame point counts in the
/// tens of thousands. Rows where <c>pc</c> or <c>view_pose</c> is null are
/// skipped and emit no frame. <c>width</c>, <c>height</c>, <c>fov_deg</c>,
/// and <c>point_size</c> are constants pinned by the first row.
/// </para>
/// </remarks>
public sealed class PcFuseRenderAggregateFunction : IAggregateFunction, IAggregateFunctionMetadata
{
    /// <inheritdoc cref="IAggregateFunctionMetadata.Name"/>
    public static string Name => "pc_fuse_render_agg";

    /// <inheritdoc/>
    string IAggregateFunction.Name => Name;

    /// <inheritdoc/>
    public static FunctionCategory Category => FunctionCategory.Aggregate;

    /// <inheritdoc/>
    public static string Description =>
        "Progressive point-cloud fusion renderer: each row renders the union of all "
        + "clouds so far from that row's camera-to-world view_pose (Float32[] or "
        + "Float64[], 16 elements), returning one Image per row. Pair with "
        + "frames_to_gif for a movie of the world building out.";

    /// <inheritdoc/>
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("pc",        DataKindMatcher.Exact(DataKind.PointCloud)),
                new ParameterSpec("view_pose", DataKindMatcher.Family(DataKindFamily.FloatFamily), IsArray: ArrayMatch.Array),
                new ParameterSpec("width",     DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
                new ParameterSpec("height",    DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
                new ParameterSpec("fov_deg",   DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Image))),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("pc",         DataKindMatcher.Exact(DataKind.PointCloud)),
                new ParameterSpec("view_pose",  DataKindMatcher.Family(DataKindFamily.FloatFamily), IsArray: ArrayMatch.Array),
                new ParameterSpec("width",      DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
                new ParameterSpec("height",     DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
                new ParameterSpec("fov_deg",    DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("point_size", DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Image))),
    ];

    /// <inheritdoc/>
    public WithinGroupSemantics WithinGroupSemantics => WithinGroupSemantics.SortModifier;

    /// <inheritdoc/>
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length is not (5 or 6))
        {
            throw new ArgumentException(
                "pc_fuse_render_agg() requires 5 or 6 arguments: "
                + "(pc PointCloud, view_pose Float32[], width Int, height Int, "
                + "fov_deg Float32 [, point_size Int]).");
        }
        if (argumentKinds[0] != DataKind.PointCloud)
        {
            throw new ArgumentException(
                $"pc_fuse_render_agg() argument 0 must be PointCloud; got {argumentKinds[0]}.");
        }
        if (argumentKinds[1] is not (DataKind.Float32 or DataKind.Float64))
        {
            throw new ArgumentException(
                $"pc_fuse_render_agg() argument 1 (view_pose) must be Float32[] or Float64[]; got {argumentKinds[1]}.");
        }
        return DataKind.Image;
    }

    /// <inheritdoc/>
    public ReturnTypeRule ReturnRule { get; } =
        ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Image));

    /// <inheritdoc/>
    public IAggregateAccumulator CreateAccumulator() => new Accumulator();

    /// <summary>
    /// Aggregate state. Cloud blobs and rendered PNG frames both live in
    /// managed memory, disconnected from the per-row arena; the only arena
    /// write is the final Array&lt;Image&gt; at
    /// <see cref="IAggregateAccumulator.ResultAsync"/>.
    /// </summary>
    private sealed class Accumulator : IAggregateAccumulator
    {
        private readonly List<byte[]> _blobs = [];
        private readonly List<byte[]> _frames = [];

        // Render constants pinned by the first accumulated row.
        private int _width = -1;
        private int _height = -1;
        private float _fovDeg = float.NaN;
        private int _pointSize = -1;

        public void Accumulate(ReadOnlySpan<DataValue> arguments, in InvocationFrame frame)
        {
            PinConstants(arguments, frame);

            if (arguments[0].IsNull || arguments[1].IsNull) return;

            float[] pose = PoseMatrixArgument.Read16(
                arguments[1], frame.Source, frame.SidecarRegistry, Name, "view_pose");

            // Copy into managed memory — disconnects from the source arena so
            // subsequent arena reuse can't corrupt accumulated state.
            byte[] blob = arguments[0].AsByteSpan(frame.Source, frame.SidecarRegistry).ToArray();
            PointCloudHeader header = PointCloudHeader.Read(blob);
            PointCloudFlags unsupported = header.Flags & ~PointCloudHeader.SupportedFlags;
            if (unsupported != PointCloudFlags.None)
            {
                throw new FunctionArgumentException(Name,
                    $"PointCloud carries unsupported attributes ({unsupported}); "
                    + "pc_fuse_render_agg currently handles position + optional color only.");
            }
            _blobs.Add(blob);

            using SKBitmap bitmap = PointCloudRasterizer.Render(
                _blobs, pose, _width, _height, _fovDeg, _pointSize);
            _frames.Add(ImageEncoder.Encode(bitmap, SKEncodedImageFormat.Png, 100));
        }

        public ValueTask MergeAsync(IAggregateAccumulator other, InvocationFrame frame)
        {
            // Each frame is a render of "everything so far" — two partial
            // accumulators can't be interleaved after the fact without
            // re-rendering every frame. Refuse rather than emit a movie with
            // points popping in out of order.
            throw new FunctionArgumentException(Name,
                "pc_fuse_render_agg cannot merge partial aggregates: frames depend on "
                + "accumulation order. Run it as a plain aggregate over an ordered input "
                + "(ORDER BY inside the aggregate), without DISTINCT.");
        }

        public ValueTask<DataValue> ResultAsync(InvocationFrame frame)
        {
            if (_frames.Count == 0)
            {
                return new ValueTask<DataValue>(DataValue.NullArrayOf(DataKind.Image));
            }
            return new ValueTask<DataValue>(
                DataValue.FromImageArray([.. _frames], frame.Target));
        }

        public void Reset()
        {
            _blobs.Clear();
            _frames.Clear();
            _width = -1;
            _height = -1;
            _fovDeg = float.NaN;
            _pointSize = -1;
        }

        /// <summary>
        /// Reads width/height/fov_deg/point_size on the first row and verifies
        /// they stay constant on every subsequent row — the frame geometry
        /// can't change mid-movie.
        /// </summary>
        private void PinConstants(ReadOnlySpan<DataValue> arguments, in InvocationFrame frame)
        {
            int width = ReadInt(arguments[2], "width");
            int height = ReadInt(arguments[3], "height");
            if (!arguments[4].TryToFloat(out float fovDeg))
            {
                throw new FunctionArgumentException(Name,
                    $"fov_deg of kind {arguments[4].Kind} could not be widened to Float32.");
            }
            int pointSize = arguments.Length >= 6 && !arguments[5].IsNull
                ? ReadInt(arguments[5], "point_size")
                : 2;

            if (_width < 0)
            {
                PcRenderFunction.ValidateDimension(width, "width");
                PcRenderFunction.ValidateDimension(height, "height");
                PcRenderFunction.ValidateFov(fovDeg);
                PcRenderFunction.ValidatePointSize(pointSize);
                _width = width;
                _height = height;
                _fovDeg = fovDeg;
                _pointSize = pointSize;
                return;
            }

            if (width != _width || height != _height || fovDeg != _fovDeg || pointSize != _pointSize)
            {
                throw new FunctionArgumentException(Name,
                    "width, height, fov_deg, and point_size must be constant across the "
                    + $"group; first row pinned ({_width}, {_height}, {_fovDeg}, {_pointSize}) "
                    + $"but a later row passed ({width}, {height}, {fovDeg}, {pointSize}).");
            }
        }

        private static int ReadInt(DataValue value, string paramName)
        {
            if (value.IsNull)
            {
                throw new FunctionArgumentException(Name, $"{paramName} must not be null.");
            }
            return value.Kind switch
            {
                DataKind.Int8 => value.AsInt8(),
                DataKind.Int16 => value.AsInt16(),
                DataKind.Int32 => value.AsInt32(),
                DataKind.Int64 => checked((int)value.AsInt64()),
                _ => throw new FunctionArgumentException(
                    Name, $"{paramName} must be an integer kind; got {value.Kind}."),
            };
        }
    }
}
