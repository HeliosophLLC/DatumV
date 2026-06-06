using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using ExecutionContext = Heliosoph.DatumV.Execution.ExecutionContext;

namespace Heliosoph.DatumV.Functions.TableValued;

/// <summary>
/// PG <c>generate_series(start, stop, stride)</c> for timestamps — emits one
/// row per stride boundary, inclusive of <c>start</c>, continuing while the
/// emitted value does not pass <c>stop</c>. The headline gap-filler for
/// time-series: pair with <c>LEFT JOIN</c> against a sparse fact table to
/// surface empty buckets.
/// </summary>
/// <remarks>
/// <para>
/// The stride is an <see cref="DataKind.Interval"/>; calendar-aware so
/// <c>'1 month'</c> walks calendar months (28–31 days each). Negative
/// strides walk backwards from <c>start</c> to <c>stop</c>. The zero
/// interval is rejected at runtime (would never terminate).
/// </para>
/// </remarks>
public sealed class GenerateSeriesTimestampFunction
    : ITableValuedFunctionMetadata, ITableValuedFunction
{
    private static readonly ColumnLookup ValueColumnLookup = new(["Value"]);

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Name => "generate_series";

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static FunctionCategory Category => FunctionCategory.Table;

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Description =>
        "Emits one row per stride boundary from start through stop (timestamp variant).";

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static IReadOnlyList<TableValuedFunctionSignatureVariant> Signatures { get; } =
    [
        new TableValuedFunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("start",  DataKindMatcher.Exact(DataKind.Timestamp)),
                new ParameterSpec("stop",   DataKindMatcher.Exact(DataKind.Timestamp)),
                new ParameterSpec("stride", DataKindMatcher.Exact(DataKind.Interval)),
            ],
            FixedOutputSchema: new Schema([new ColumnInfo("Value", DataKind.Timestamp, nullable: false)])),
        new TableValuedFunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("start",  DataKindMatcher.Exact(DataKind.TimestampTz)),
                new ParameterSpec("stop",   DataKindMatcher.Exact(DataKind.TimestampTz)),
                new ParameterSpec("stride", DataKindMatcher.Exact(DataKind.Interval)),
            ],
            FixedOutputSchema: new Schema([new ColumnInfo("Value", DataKind.TimestampTz, nullable: false)])),
    ];

    string ITableValuedFunction.Name => Name;

    /// <inheritdoc />
    public Schema ValidateArguments(
        ReadOnlySpan<DataKind> argumentKinds,
        ReadOnlySpan<DataValue?> constantArguments,
        IValueStore constantStore,
        CancellationToken cancellationToken)
    {
        if (argumentKinds.Length != 3)
        {
            throw new FunctionArgumentException(Name,
                "requires 3 arguments: generate_series(start, stop, stride).");
        }
        if (argumentKinds[2] != DataKind.Interval)
        {
            throw new FunctionArgumentException(Name,
                "stride must be an Interval (use the numeric range() TVF for integer sequences).");
        }
        if (argumentKinds[0] != argumentKinds[1])
        {
            throw new FunctionArgumentException(Name,
                "start and stop must be the same temporal kind (both Timestamp or both TimestampTz).");
        }
        if (argumentKinds[0] is not (DataKind.Timestamp or DataKind.TimestampTz))
        {
            throw new FunctionArgumentException(Name,
                "start / stop must be Timestamp or TimestampTz.");
        }
        return new Schema([new ColumnInfo("Value", argumentKinds[0], nullable: false)]);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RowBatch> ExecuteAsync(
        ValueRef[] arguments, ExecutionContext context)
    {
        if (arguments.Length != 3)
        {
            throw new ArgumentException(
                "generate_series() requires 3 arguments: generate_series(start, stop, stride).");
        }

        Interval stride = arguments[2].AsInterval();
        if (stride.Months == 0 && stride.Days == 0 && stride.Microseconds == 0)
        {
            throw new ArgumentException("generate_series() stride must be non-zero.");
        }
        bool ascending = IsAscending(stride);

        CancellationToken cancellationToken = context.CancellationToken;
        RowBatch? batch = null;

        if (arguments[0].Kind == DataKind.Timestamp)
        {
            DateTime current = arguments[0].AsTimestamp();
            DateTime stop = arguments[1].AsTimestamp();
            while (ascending ? current <= stop : current >= stop)
            {
                cancellationToken.ThrowIfCancellationRequested();
                batch ??= context.RentRowBatch(ValueColumnLookup);
                batch.Add([DataValue.FromTimestamp(current)]);
                if (batch.IsFull)
                {
                    yield return batch;
                    batch = null;
                }
                DateTime next = stride.AddTo(current);
                // Guard against an interval that fails to advance — would
                // otherwise infinite-loop. Months-only strides on edge dates
                // can theoretically map back onto themselves, though PG's
                // AddMonths semantics make this practically unreachable.
                if (next == current) break;
                current = next;
            }
        }
        else
        {
            DateTimeOffset current = arguments[0].AsTimestampTz();
            DateTimeOffset stop = arguments[1].AsTimestampTz();
            while (ascending ? current <= stop : current >= stop)
            {
                cancellationToken.ThrowIfCancellationRequested();
                batch ??= context.RentRowBatch(ValueColumnLookup);
                batch.Add([DataValue.FromTimestampTz(current)]);
                if (batch.IsFull)
                {
                    yield return batch;
                    batch = null;
                }
                DateTimeOffset next = stride.AddTo(current);
                if (next == current) break;
                current = next;
            }
        }

        if (batch is not null)
        {
            yield return batch;
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Detects whether <paramref name="stride"/> walks forward or backward.
    /// Uses canonical-month / 24-hour-day weighting so a mixed-component
    /// interval like <c>'1 month -5 days'</c> resolves to the dominant sign.
    /// </summary>
    private static bool IsAscending(Interval stride)
    {
        long total = (long)stride.Months * Interval.DaysPerMonth * Interval.MicrosPerDay
            + (long)stride.Days * Interval.MicrosPerDay
            + stride.Microseconds;
        return total > 0;
    }
}
