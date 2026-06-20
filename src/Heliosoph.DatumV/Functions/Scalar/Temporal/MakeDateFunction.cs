using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Temporal;

/// <summary>
/// PG <c>make_date(year, month, day)</c> — builds a <see cref="DataKind.Date"/>
/// from integer components. Component values that fall outside the valid
/// Gregorian calendar surface as <see cref="ExecutionException"/>.
/// </summary>
public sealed class MakeDateFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "make_date";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Temporal;

    /// <inheritdoc />
    public static string Description =>
        "Builds a Date from integer year/month/day components.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("year",  DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("month", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("day",   DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Date)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<MakeDateFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull || args[2].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Date));
        }

        int year  = args[0].TryToInt32(out int y) ? y : 0;
        int month = args[1].TryToInt32(out int m) ? m : 0;
        int day   = args[2].TryToInt32(out int d) ? d : 0;

        try
        {
            return new ValueTask<ValueRef>(ValueRef.FromDate(new DateOnly(year, month, day)));
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new ExecutionException(
                $"make_date: invalid date ({year:D4}-{month:D2}-{day:D2}).", ex);
        }
    }

    /// <inheritdoc />
    public bool IsPure => true;
}
