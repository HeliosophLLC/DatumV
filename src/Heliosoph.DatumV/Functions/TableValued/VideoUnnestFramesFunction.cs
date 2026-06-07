using Heliosoph.DatumV.DatumFile.Sidecar;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using ExecutionContext = Heliosoph.DatumV.Execution.ExecutionContext;

namespace Heliosoph.DatumV.Functions.TableValued;

/// <summary>
/// <c>video_unnest_frames(path[, start_frame[, stride[, max_frames]]]) → table</c>.
/// Registers the video at <c>path</c> with the per-query
/// <see cref="VideoRegistry"/> and emits one row per requested frame —
/// columns <c>(frame_index INT, frame VideoFrame)</c>. Frame handles are inline
/// <see cref="DataKind.VideoFrame"/> values; pixel materialisation is deferred
/// until a downstream consumer routes the handle through the registry.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Lazy emission.</strong> This function does no decoding. It registers
/// the path once, reads container metadata to learn the total frame count, and
/// emits handles. The cost of emitting all N frames is O(N) handle-construction
/// plus one FFmpeg open + stream-info probe. Actual frame decode happens when
/// downstream code calls <see cref="VideoRegistry.Materialize"/>, typically
/// from an image-consuming function.
/// </para>
/// <para>
/// <strong>Sequential downstream consumption.</strong> The registry's warm
/// decoder is optimised for in-order frame access; consumers that iterate
/// the emitted rows top-to-bottom hit the fast path (~11 ms/frame on the
/// reference 1080p H.264 clip). Out-of-order access pays a seek-to-head
/// penalty per non-sequential read.
/// </para>
/// <para>
/// <strong>Zero-based frame indices.</strong> <c>frame_index</c> is the
/// container-native frame address — the same coordinate FFmpeg, ffprobe,
/// and <see cref="VideoRegistry.Materialize"/> use, where frame 0 is the
/// first frame. This intentionally departs from PostgreSQL's 1-based
/// ordinality convention (<c>generate_series</c>, <c>WITH ORDINALITY</c>,
/// array subscripts): shifting it would force <c>start_frame</c> and
/// <c>max_frames</c> to disagree with every external video tool the user
/// compares against. Callers who want a 1-based row number can layer
/// <c>ROW_NUMBER() OVER ()</c> over the output — that produces an
/// ordinality column distinct from the underlying frame address.
/// </para>
/// </remarks>
public sealed class VideoUnnestFramesFunction : ITableValuedFunctionMetadata, ITableValuedFunction
{
    private static readonly ColumnLookup OutputColumnLookup = new(["frame_index", "frame"]);

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Name => "video_unnest_frames";

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static FunctionCategory Category => FunctionCategory.Table;

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Description =>
        "Enumerates frames of a video file as lazy VideoFrame handles: " +
        "video_unnest_frames(path STRING [, start_frame INT [, stride INT [, max_frames INT]]]). " +
        "Defaults: start_frame=0, stride=1, max_frames=-1 (use the container's reported frame count). " +
        "Columns: (frame_index INT, frame VideoFrame). Pixel decoding is deferred to the consumer.";

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static IReadOnlyList<TableValuedFunctionSignatureVariant> Signatures { get; } =
    [
        new TableValuedFunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("source", DataKindMatcher.OneOf(DataKind.String, DataKind.Video)),
                new ParameterSpec("start_frame", DataKindMatcher.Family(DataKindFamily.NumericScalar), IsOptional: true),
                new ParameterSpec("stride", DataKindMatcher.Family(DataKindFamily.NumericScalar), IsOptional: true),
                new ParameterSpec("max_frames", DataKindMatcher.Family(DataKindFamily.NumericScalar), IsOptional: true),
            ],
            FixedOutputSchema: new Schema(
            [
                new ColumnInfo("frame_index", DataKind.Int32, nullable: false),
                new ColumnInfo("frame", DataKind.VideoFrame, nullable: false),
            ])),
    ];

    string ITableValuedFunction.Name => Name;

    /// <inheritdoc />
    public Schema ValidateArguments(
        ReadOnlySpan<DataKind> argumentKinds,
        ReadOnlySpan<DataValue?> constantArguments,
        IValueStore constantStore,
        CancellationToken cancellationToken)
    {
        if (argumentKinds.Length is < 1 or > 4)
        {
            throw new FunctionArgumentException(Name,
                "requires 1 to 4 arguments: video_unnest_frames(source [, start_frame INT [, stride INT [, max_frames INT]]]). " +
                "Source may be a STRING file path or a Video column value.");
        }
        if (argumentKinds[0] is not (DataKind.String or DataKind.Video))
        {
            throw new FunctionArgumentException(Name,
                "argument 1 (source) must be STRING (file path) or Video.");
        }
        for (int i = 1; i < argumentKinds.Length; i++)
        {
            if (!DataValueComparer.IsNumericScalar(argumentKinds[i]))
            {
                throw new FunctionArgumentException(Name,
                    $"argument {i + 1} must be an integer.");
            }
        }
        return new Schema(
        [
            new ColumnInfo("frame_index", DataKind.Int32, nullable: false),
            new ColumnInfo("frame", DataKind.VideoFrame, nullable: false),
        ]);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RowBatch> ExecuteAsync(ValueRef[] arguments, ExecutionContext context)
    {
        if (arguments.Length is < 1 or > 4)
        {
            throw new ArgumentException(
                "video_unnest_frames requires 1 to 4 arguments: " +
                "(path [, start_frame [, stride [, max_frames]]]).");
        }

        int startFrame = arguments.Length >= 2 ? (int)arguments[1].ToInt64() : 0;
        int stride = arguments.Length >= 3 ? (int)arguments[2].ToInt64() : 1;
        int maxFrames = arguments.Length >= 4 ? (int)arguments[3].ToInt64() : -1;

        if (startFrame < 0)
        {
            throw new FunctionArgumentException(Name,
                $"start_frame must be >= 0 (got {startFrame}).");
        }
        if (stride < 1)
        {
            throw new FunctionArgumentException(Name,
                $"stride must be >= 1 (got {stride}).");
        }

        // Register once for the whole emission. The id stays valid for the
        // query's lifetime so downstream consumers can call back into the
        // registry to materialise pixels.
        uint videoId = RegisterSource(arguments[0], context);
        VideoMetadata metadata = context.VideoRegistry.GetMetadata(videoId);

        long endExclusive = ComputeEndExclusive(metadata, startFrame, stride, maxFrames);

        CancellationToken cancellationToken = context.CancellationToken;
        RowBatch? batch = null;
        for (long frameIndex = startFrame; frameIndex < endExclusive; frameIndex += stride)
        {
            cancellationToken.ThrowIfCancellationRequested();
            batch ??= context.RentRowBatch(OutputColumnLookup);
            batch.Add(
            [
                DataValue.FromInt32((int)frameIndex),
                DataValue.FromVideoFrame(videoId, (int)frameIndex),
            ]);
            if (batch.IsFull)
            {
                yield return batch;
                batch = null;
            }
        }

        if (batch is not null && batch.Count > 0)
        {
            yield return batch;
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Routes the source argument (STRING path or Video DataValue) to the
    /// appropriate <see cref="VideoRegistry"/> entry point and returns the
    /// resulting video id.
    /// </summary>
    private static uint RegisterSource(ValueRef source, ExecutionContext context)
    {
        if (source.Kind == DataKind.String)
        {
            return context.VideoRegistry.RegisterPath(source.AsString());
        }
        if (source.Kind != DataKind.Video)
        {
            throw new FunctionArgumentException(Name,
                $"argument 1 (source) must be STRING or Video; got {source.Kind}.");
        }
        DataValue video = source.ToDataValue(context.Store);
        if (video.IsInSidecar)
        {
            IBlobSource blobSource = context.SidecarRegistry.Resolve(video.SidecarStoreId)
                ?? throw new FunctionArgumentException(Name,
                    $"sidecar storeId={video.SidecarStoreId} is not registered with the current query's SidecarRegistry. " +
                    "The Video column's source sidecar must be opened by the catalog before this function runs.");
            return context.VideoRegistry.RegisterSidecar(blobSource, video.SidecarOffset, video.SidecarLength);
        }
        // Arena-backed Video — copy the encoded bytes into a managed buffer
        // and hand them to the registry's in-memory path. The copy is
        // unavoidable: FFmpeg's IOContext drives a Stream, and the arena
        // bytes may be reused once this batch is recycled.
        byte[] bytes = video.AsByteSpan(context.Store).ToArray();
        return context.VideoRegistry.RegisterBytes(bytes);
    }

    /// <summary>
    /// Resolves the exclusive upper bound for the emitted frame range, honoring
    /// <paramref name="maxFrames"/> when set and falling back to the container's
    /// reported frame count otherwise.
    /// </summary>
    private long ComputeEndExclusive(VideoMetadata metadata, int startFrame, int stride, int maxFrames)
    {
        if (maxFrames > 0)
        {
            // Bound by user-specified count: emit maxFrames rows (each `stride` apart) starting at startFrame.
            return (long)startFrame + (long)maxFrames * stride;
        }
        if (metadata.FrameCount is not long frameCount)
        {
            throw new FunctionArgumentException(Name,
                "the source container does not report a frame count, so max_frames must be supplied explicitly.");
        }
        return frameCount;
    }
}
