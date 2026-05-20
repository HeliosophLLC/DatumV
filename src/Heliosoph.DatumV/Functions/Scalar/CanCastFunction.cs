using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar;

/// <summary>
/// Returns <see langword="true"/> when <c>cast(value, type)</c> would succeed
/// for the supplied pair, <see langword="false"/> when it would throw or fail
/// to parse. Shares the dispatch table with <see cref="CastFunction"/> — adding
/// a pair there enables it here automatically.
/// </summary>
/// <remarks>
/// Null values are reported as castable (a typed null is always producible).
/// Array sources cannot be flattened to scalar targets, and scalar sources
/// cannot be widened to array targets, matching <see cref="CastFunction"/>.
/// </remarks>
public sealed class CanCastFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "can_cast";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Conversion;

    /// <inheritdoc />
    public static string Description =>
        "Returns true when cast(value, type) would succeed, false on unsupported pairs or parse failures.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value", DataKindMatcher.Any),
                new ParameterSpec(
                    "target",
                    DataKindMatcher.OneOf(DataKind.Type, DataKind.String)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Boolean)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<CanCastFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef input = args[0];
        (DataKind targetKind, bool targetIsArray) = CastFunction.ResolveTarget(args[1]);

        // Typed null is always producible regardless of source/target kind pair.
        if (input.IsNull)
            return new ValueTask<ValueRef>(ValueRef.FromBoolean(true));

        if (targetIsArray)
        {
            return new ValueTask<ValueRef>(
                ValueRef.FromBoolean(input.IsArray && input.Kind == targetKind));
        }

        if (input.IsArray)
            return new ValueTask<ValueRef>(ValueRef.FromBoolean(false));

        if (input.Kind == targetKind)
            return new ValueTask<ValueRef>(ValueRef.FromBoolean(true));

        // Numeric targets: bounds-check up front because CastFunction's
        // MakeNumeric uses unchecked primitive casts that wrap/truncate
        // silently. Without this, can_cast(5000, UInt8) would return true
        // even though the result would be a corrupted byte.
        if (DataValueComparer.IsNumericScalar(targetKind)
            && input.TryToDouble(out double inputDouble)
            && !WouldFitNumericRange(inputDouble, targetKind))
        {
            return new ValueTask<ValueRef>(ValueRef.FromBoolean(false));
        }

        bool ok = CastFunction.TryCastCore(input, targetKind, out _);
        return new ValueTask<ValueRef>(ValueRef.FromBoolean(ok));
    }

    private static bool WouldFitNumericRange(double value, DataKind targetKind)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return false;
        return targetKind switch
        {
            DataKind.Int8    => value >= sbyte.MinValue  && value <= sbyte.MaxValue,
            DataKind.UInt8   => value >= 0                && value <= byte.MaxValue,
            DataKind.Int16   => value >= short.MinValue  && value <= short.MaxValue,
            DataKind.UInt16  => value >= 0                && value <= ushort.MaxValue,
            DataKind.Int32   => value >= int.MinValue    && value <= int.MaxValue,
            DataKind.UInt32  => value >= 0                && value <= uint.MaxValue,
            DataKind.Int64   => value >= long.MinValue   && value <= long.MaxValue,
            DataKind.UInt64  => value >= 0                && value <= ulong.MaxValue,
            DataKind.Int128 or DataKind.UInt128 => true,
            DataKind.Float16 => value >= -65504.0         && value <= 65504.0,
            DataKind.Float32 => value >= float.MinValue  && value <= float.MaxValue,
            DataKind.Float64 => true,
            DataKind.Decimal => value >= (double)decimal.MinValue && value <= (double)decimal.MaxValue,
            _ => true,
        };
    }
}
