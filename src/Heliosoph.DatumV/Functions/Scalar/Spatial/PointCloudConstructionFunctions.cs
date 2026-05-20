using System.Numerics;

using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Model.Spatial;

namespace Heliosoph.DatumV.Functions.Scalar.Spatial;

/// <summary>
/// <c>pc_empty() → PointCloud</c>. Builds a zero-point, position-only,
/// unorganized PointCloud — the seed value for a <c>SCAN</c> fold whose
/// accumulator is a growing PointCloud (e.g. iterating depth-derived clouds
/// across frames and concatenating with <c>pc_fuse</c>).
/// </summary>
/// <remarks>
/// The header carries <see cref="PointCloudCoordinateFrame.Unspecified"/> so
/// the first non-empty fuse adopts the producer's frame without contention.
/// Bbox corners default to the origin; consumers should treat them as
/// meaningless until at least one point has been fused in.
/// </remarks>
public sealed class PcEmptyFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "pc_empty";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Spatial;

    /// <inheritdoc />
    public static string Description =>
        "Returns an empty, position-only, unorganized PointCloud — useful as the "
        + "SCAN INIT seed when folding multiple clouds (e.g. per-frame depth "
        + "unprojections) into one growing accumulator via pc_fuse.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.PointCloud)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<PcEmptyFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        byte[] blob = new byte[PointCloudHeader.SizeBytes];
        PointCloudHeader header = new(
            Version: PointCloudHeader.CurrentVersion,
            Flags: PointCloudFlags.None,
            CoordinateFrame: PointCloudCoordinateFrame.Unspecified,
            PointCount: 0,
            BboxMin: Vector3.Zero,
            BboxMax: Vector3.Zero,
            Width: 0,
            Height: 0);
        header.Write(blob);
        return new ValueTask<ValueRef>(ValueRef.FromPointCloud(blob));
    }
}
