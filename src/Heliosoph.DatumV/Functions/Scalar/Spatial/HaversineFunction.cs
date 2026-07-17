using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Spatial;

/// <summary>
/// Great-circle distance in meters between two WGS-84 coordinates:
/// <c>haversine(lat1, lon1, lat2, lon2) → Float64</c>. Null in any argument
/// propagates to null output. Latitudes/longitudes are decimal degrees —
/// the shape geocoders produce — so "within 10 miles" is
/// <c>haversine(...) &lt; 16093.4</c> with no projection step.
/// </summary>
/// <remarks>
/// Spherical-earth model on the IUGG mean radius (6371008.8 m), matching
/// PostGIS's <c>ST_DistanceSphere</c>; error vs. the WGS-84 ellipsoid stays
/// under ~0.5%, which is noise at address-data accuracy. For planar data
/// (projected coordinates, model space) use <c>distance()</c> instead.
/// </remarks>
public sealed class HaversineFunction : IFunction, IScalarFunction
{
    private const double EarthRadiusMeters = 6_371_008.8;

    /// <inheritdoc />
    public static string Name => "haversine";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Spatial;

    /// <inheritdoc />
    public static string Description =>
        "Great-circle distance in meters between two WGS-84 coordinates: "
        + "haversine(lat1, lon1, lat2, lon2) → Float64.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("lat1", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("lon1", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("lat2", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("lon2", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float64)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<HaversineFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        Span<double> values = stackalloc double[4];
        for (int i = 0; i < 4; i++)
        {
            if (args[i].IsNull || !args[i].TryToDouble(out values[i]))
            {
                return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Float64));
            }
        }

        double lat1 = ToRadians(values[0]);
        double lon1 = ToRadians(values[1]);
        double lat2 = ToRadians(values[2]);
        double lon2 = ToRadians(values[3]);

        double sinHalfLat = System.Math.Sin((lat2 - lat1) / 2);
        double sinHalfLon = System.Math.Sin((lon2 - lon1) / 2);
        double h = (sinHalfLat * sinHalfLat)
            + (System.Math.Cos(lat1) * System.Math.Cos(lat2) * sinHalfLon * sinHalfLon);
        double distance = 2 * EarthRadiusMeters * System.Math.Asin(System.Math.Min(1.0, System.Math.Sqrt(h)));

        return new ValueTask<ValueRef>(ValueRef.FromFloat64(distance));
    }

    private static double ToRadians(double degrees) => degrees * System.Math.PI / 180.0;
}
