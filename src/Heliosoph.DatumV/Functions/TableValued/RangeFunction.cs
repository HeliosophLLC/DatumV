using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using ExecutionContext = Heliosoph.DatumV.Execution.ExecutionContext;

namespace Heliosoph.DatumV.Functions.TableValued;

/// <summary>
/// DuckDB-compatible <c>range(start, stop[, step])</c> — emits a half-open
/// sequence <c>[start, stop)</c> as a single-column relation. Numeric and
/// temporal forms share the engine with <see cref="GenerateSeriesFunction"/>;
/// the only behavioural difference is the upper-bound exclusion here.
/// </summary>
/// <remarks>
/// <para>
/// Numeric form (Int32 / Int64 / Float64; mixed kinds widen to the widest
/// argument): <c>FROM range(0, 5)</c> yields <c>0, 1, 2, 3, 4</c>. Step
/// defaults to 1 and must be non-zero; a step pointing away from <c>stop</c>
/// yields zero rows.
/// </para>
/// <para>
/// Temporal form (Timestamp / TimestampTz with an <see cref="DataKind.Interval"/>
/// stride): <c>FROM range(TIMESTAMP '...', TIMESTAMP '...', INTERVAL '1 hour')</c>
/// walks calendar-aware boundaries and excludes the upper bound. Pair with
/// <see cref="GenerateSeriesFunction"/> when you want the upper bound included.
/// </para>
/// </remarks>
public sealed class RangeFunction : ITableValuedFunctionMetadata, ITableValuedFunction
{
    private const string ColumnName = "value";
    private static readonly ColumnLookup OutputColumnLookup = new([ColumnName]);

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Name => "range";

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static FunctionCategory Category => FunctionCategory.Table;

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Description =>
        "Generates a half-open sequence [start, stop): range(start, stop[, step]) for numerics, " +
        "range(start, stop, stride) for timestamps. Step/stride pointing away from stop yields zero rows.";

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static IReadOnlyList<TableValuedFunctionSignatureVariant> Signatures { get; } =
    [
        new TableValuedFunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("start", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("stop",  DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("step",  DataKindMatcher.Family(DataKindFamily.NumericScalar), IsOptional: true),
            ],
            FixedOutputSchema: null),
        new TableValuedFunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("start",  DataKindMatcher.Exact(DataKind.Timestamp)),
                new ParameterSpec("stop",   DataKindMatcher.Exact(DataKind.Timestamp)),
                new ParameterSpec("stride", DataKindMatcher.Exact(DataKind.Interval)),
            ],
            FixedOutputSchema: new Schema([new ColumnInfo(ColumnName, DataKind.Timestamp, nullable: false)])),
        new TableValuedFunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("start",  DataKindMatcher.Exact(DataKind.TimestampTz)),
                new ParameterSpec("stop",   DataKindMatcher.Exact(DataKind.TimestampTz)),
                new ParameterSpec("stride", DataKindMatcher.Exact(DataKind.Interval)),
            ],
            FixedOutputSchema: new Schema([new ColumnInfo(ColumnName, DataKind.TimestampTz, nullable: false)])),
    ];

    string ITableValuedFunction.Name => Name;

    /// <inheritdoc />
    public Schema ValidateArguments(
        ReadOnlySpan<DataKind> argumentKinds,
        ReadOnlySpan<DataValue?> constantArguments,
        IValueStore constantStore,
        CancellationToken cancellationToken) =>
        SequenceGenerator.ValidateArguments(Name, ColumnName, argumentKinds);

    /// <inheritdoc />
    public IAsyncEnumerable<RowBatch> ExecuteAsync(ValueRef[] arguments, ExecutionContext context) =>
        SequenceGenerator.ExecuteAsync(Name, OutputColumnLookup, arguments, context, inclusive: false);
}
