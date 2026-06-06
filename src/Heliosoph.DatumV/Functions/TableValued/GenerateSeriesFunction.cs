using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using ExecutionContext = Heliosoph.DatumV.Execution.ExecutionContext;

namespace Heliosoph.DatumV.Functions.TableValued;

/// <summary>
/// PostgreSQL <c>generate_series(start, stop[, step])</c> — emits a closed
/// sequence <c>[start, stop]</c> as a single-column relation. Numeric and
/// temporal forms share the engine with <see cref="RangeFunction"/>; the
/// only behavioural difference is the upper-bound inclusion here.
/// </summary>
/// <remarks>
/// <para>
/// Numeric form (Int32 / Int64 / Float64; mixed kinds widen to the widest
/// argument): <c>FROM generate_series(0, 5)</c> yields <c>0, 1, 2, 3, 4, 5</c>.
/// Step defaults to 1 and must be non-zero; a step pointing away from
/// <c>stop</c> yields zero rows.
/// </para>
/// <para>
/// Temporal form (Timestamp / TimestampTz with an <see cref="DataKind.Interval"/>
/// stride): the headline gap-filler for time-series — pair with a
/// <c>LEFT JOIN</c> against a sparse fact table to surface empty buckets.
/// Month strides walk calendar months (28–31 days each).
/// </para>
/// </remarks>
public sealed class GenerateSeriesFunction : ITableValuedFunctionMetadata, ITableValuedFunction
{
    private const string ColumnName = "value";
    private static readonly ColumnLookup OutputColumnLookup = new([ColumnName]);

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Name => "generate_series";

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static FunctionCategory Category => FunctionCategory.Table;

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Description =>
        "Generates an inclusive sequence [start, stop]: generate_series(start, stop[, step]) for " +
        "numerics, generate_series(start, stop, stride) for timestamps. Step/stride pointing away " +
        "from stop yields zero rows.";

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
        SequenceGenerator.ExecuteAsync(Name, OutputColumnLookup, arguments, context, inclusive: true);
}
