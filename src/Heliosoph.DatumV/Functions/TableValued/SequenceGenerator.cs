using Heliosoph.DatumV.Model;
using ExecutionContext = Heliosoph.DatumV.Execution.ExecutionContext;

namespace Heliosoph.DatumV.Functions.TableValued;

/// <summary>
/// Shared validation + iteration engine for the sequence-generating TVFs
/// (<see cref="RangeFunction"/>, <see cref="GenerateSeriesFunction"/>).
/// The two functions differ only in whether the upper bound is included in
/// the emitted rows; everything else — argument validation, numeric kind
/// promotion, wrong-direction guards, the drift-avoidance float counter,
/// the calendar-aware interval walk, the cancellation hook, and the batch
/// rental/yield cadence — is the same across both and lives here.
/// </summary>
/// <remarks>
/// <para>
/// "Wrong direction" (positive step but <c>start &gt; stop</c>, or negative
/// step but <c>start &lt; stop</c>) produces an empty result rather than
/// throwing — matches PostgreSQL <c>generate_series</c> and DuckDB
/// <c>range</c>. Zero step throws because the iteration would never
/// terminate.
/// </para>
/// </remarks>
internal static class SequenceGenerator
{
    /// <summary>
    /// Plan-time validation. Resolves the output schema (column kind follows
    /// the widest numeric input or the temporal input kind) and rejects
    /// argument shapes that don't satisfy any signature variant.
    /// </summary>
    public static Schema ValidateArguments(
        string functionName,
        string columnName,
        ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length is not (2 or 3))
        {
            throw new FunctionArgumentException(functionName,
                $"requires 2 or 3 arguments: {functionName}(start, stop[, step]).");
        }

        DataKind first = argumentKinds[0];
        if (first is DataKind.Timestamp or DataKind.TimestampTz)
        {
            if (argumentKinds.Length != 3)
            {
                throw new FunctionArgumentException(functionName,
                    "temporal form requires 3 arguments: " +
                    $"{functionName}(start, stop, stride).");
            }
            if (argumentKinds[1] != first)
            {
                throw new FunctionArgumentException(functionName,
                    "start and stop must be the same temporal kind (both Timestamp or both TimestampTz).");
            }
            if (argumentKinds[2] != DataKind.Interval)
            {
                throw new FunctionArgumentException(functionName,
                    "stride must be an Interval for the temporal form.");
            }
            return new Schema([new ColumnInfo(columnName, first, nullable: false)]);
        }

        if (!DataValueComparer.IsNumericScalar(first))
        {
            throw new FunctionArgumentException(functionName,
                $"argument 1 must be numeric, Timestamp, or TimestampTz (got {first}).");
        }

        DataKind outputKind = DataKind.Int32;
        for (int i = 0; i < argumentKinds.Length; i++)
        {
            if (!DataValueComparer.IsNumericScalar(argumentKinds[i]))
            {
                throw new FunctionArgumentException(functionName,
                    $"argument {i + 1} must be a numeric scalar (got {argumentKinds[i]}).");
            }
            outputKind = PromoteKind(outputKind, argumentKinds[i]);
        }
        return new Schema([new ColumnInfo(columnName, outputKind, nullable: false)]);
    }

    /// <summary>
    /// Execution-time dispatch. Routes to the correct emitter based on the
    /// resolved output kind. The <paramref name="inclusive"/> flag selects
    /// generate_series (<see langword="true"/>) vs range (<see langword="false"/>)
    /// semantics on the upper bound.
    /// </summary>
    public static IAsyncEnumerable<RowBatch> ExecuteAsync(
        string functionName,
        ColumnLookup lookup,
        ValueRef[] arguments,
        ExecutionContext context,
        bool inclusive)
    {
        if (arguments.Length is not (2 or 3))
        {
            throw new ArgumentException(
                $"{functionName}() requires 2 or 3 arguments.");
        }

        // PG semantics: any NULL argument yields zero rows.
        for (int i = 0; i < arguments.Length; i++)
        {
            if (arguments[i].IsNull) return EmptyAsync();
        }

        DataKind first = arguments[0].Kind;
        if (first is DataKind.Timestamp)
        {
            Interval stride = arguments[2].AsInterval();
            RejectZeroStride(functionName, stride);
            return EmitTimestamp(
                arguments[0].AsTimestamp(),
                arguments[1].AsTimestamp(),
                stride, inclusive, lookup, context);
        }
        if (first is DataKind.TimestampTz)
        {
            Interval stride = arguments[2].AsInterval();
            RejectZeroStride(functionName, stride);
            return EmitTimestampTz(
                arguments[0].AsTimestampTz(),
                arguments[1].AsTimestampTz(),
                stride, inclusive, lookup, context);
        }

        DataKind outputKind = DataKind.Int32;
        for (int i = 0; i < arguments.Length; i++)
        {
            outputKind = PromoteKind(outputKind, arguments[i].Kind);
        }

        if (IsIntegerKind(outputKind))
        {
            long start = arguments[0].ToInt64();
            long stop = arguments[1].ToInt64();
            long step = arguments.Length == 3 ? arguments[2].ToInt64() : 1L;
            if (step == 0L)
            {
                throw new ArgumentException($"{functionName}() step cannot be zero.");
            }
            return EmitInt(start, stop, step, inclusive, outputKind, lookup, context);
        }
        else
        {
            double start = arguments[0].ToDouble();
            double stop = arguments[1].ToDouble();
            double step = arguments.Length == 3 ? arguments[2].ToDouble() : 1.0;
            if (step == 0.0)
            {
                throw new ArgumentException($"{functionName}() step cannot be zero.");
            }
            return EmitFloat(start, stop, step, inclusive, outputKind, lookup, context);
        }
    }

    private static async IAsyncEnumerable<RowBatch> EmptyAsync()
    {
        await Task.CompletedTask;
        yield break;
    }

    private static void RejectZeroStride(string functionName, Interval stride)
    {
        if (stride.Months == 0 && stride.Days == 0 && stride.Microseconds == 0)
        {
            throw new ArgumentException($"{functionName}() stride must be non-zero.");
        }
    }

    private static async IAsyncEnumerable<RowBatch> EmitInt(
        long start, long stop, long step, bool inclusive,
        DataKind outputKind,
        ColumnLookup lookup,
        ExecutionContext context)
    {
        if ((step > 0 && start > stop) || (step < 0 && start < stop))
        {
            yield break;
        }

        CancellationToken cancellationToken = context.CancellationToken;
        RowBatch? batch = null;

        for (long current = start;
             inclusive
                ? (step > 0 ? current <= stop : current >= stop)
                : (step > 0 ? current < stop : current > stop);
             current += step)
        {
            cancellationToken.ThrowIfCancellationRequested();
            batch ??= context.RentRowBatch(lookup);
            batch.Add([DataValueFromLong(current, outputKind)]);
            if (batch.IsFull)
            {
                yield return batch;
                batch = null;
            }
        }

        if (batch is not null)
        {
            yield return batch;
        }
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<RowBatch> EmitFloat(
        double start, double stop, double step, bool inclusive,
        DataKind outputKind,
        ColumnLookup lookup,
        ExecutionContext context)
    {
        if ((step > 0 && start > stop) || (step < 0 && start < stop))
        {
            yield break;
        }

        CancellationToken cancellationToken = context.CancellationToken;
        RowBatch? batch = null;
        double tolerance = System.Math.Abs(step) * 1e-9;

        for (int i = 0; ; i++)
        {
            double current = start + i * step;
            bool past = inclusive
                ? (step > 0 ? current > stop + tolerance : current < stop - tolerance)
                : (step > 0 ? current >= stop - tolerance : current <= stop + tolerance);
            if (past) break;

            cancellationToken.ThrowIfCancellationRequested();
            batch ??= context.RentRowBatch(lookup);
            batch.Add([DataValueFromDouble(current, outputKind)]);
            if (batch.IsFull)
            {
                yield return batch;
                batch = null;
            }
        }

        if (batch is not null)
        {
            yield return batch;
        }
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<RowBatch> EmitTimestamp(
        DateTime start, DateTime stop, Interval stride, bool inclusive,
        ColumnLookup lookup,
        ExecutionContext context)
    {
        bool ascending = IsAscending(stride);
        if ((ascending && start > stop) || (!ascending && start < stop))
        {
            yield break;
        }

        CancellationToken cancellationToken = context.CancellationToken;
        RowBatch? batch = null;
        DateTime current = start;

        while (inclusive
            ? (ascending ? current <= stop : current >= stop)
            : (ascending ? current < stop : current > stop))
        {
            cancellationToken.ThrowIfCancellationRequested();
            batch ??= context.RentRowBatch(lookup);
            batch.Add([DataValue.FromTimestamp(current)]);
            if (batch.IsFull)
            {
                yield return batch;
                batch = null;
            }

            DateTime next = stride.AddTo(current);
            // Months-only strides on edge dates can in theory map back onto
            // themselves; bail out rather than infinite-loop.
            if (next == current) break;
            current = next;
        }

        if (batch is not null)
        {
            yield return batch;
        }
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<RowBatch> EmitTimestampTz(
        DateTimeOffset start, DateTimeOffset stop, Interval stride, bool inclusive,
        ColumnLookup lookup,
        ExecutionContext context)
    {
        bool ascending = IsAscending(stride);
        if ((ascending && start > stop) || (!ascending && start < stop))
        {
            yield break;
        }

        CancellationToken cancellationToken = context.CancellationToken;
        RowBatch? batch = null;
        DateTimeOffset current = start;

        while (inclusive
            ? (ascending ? current <= stop : current >= stop)
            : (ascending ? current < stop : current > stop))
        {
            cancellationToken.ThrowIfCancellationRequested();
            batch ??= context.RentRowBatch(lookup);
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

        if (batch is not null)
        {
            yield return batch;
        }
        await Task.CompletedTask;
    }

    private static bool IsAscending(Interval stride)
    {
        long total = (long)stride.Months * Interval.DaysPerMonth * Interval.MicrosPerDay
            + (long)stride.Days * Interval.MicrosPerDay
            + stride.Microseconds;
        return total > 0;
    }

    private static bool IsIntegerKind(DataKind kind) =>
        kind is DataKind.Int32 or DataKind.Int64;

    private static DataKind CanonicalOutputKind(DataKind kind) => kind switch
    {
        DataKind.Int8 or DataKind.UInt8 or DataKind.Int16 or DataKind.UInt16 or DataKind.Int32 => DataKind.Int32,
        DataKind.UInt32 or DataKind.Int64 or DataKind.UInt64 => DataKind.Int64,
        DataKind.Float16 or DataKind.Float32 => DataKind.Float32,
        DataKind.Float64 => DataKind.Float64,
        _ => throw new InvalidOperationException($"Not a numeric kind: {kind}."),
    };

    private static DataKind PromoteKind(DataKind a, DataKind b)
    {
        DataKind ca = CanonicalOutputKind(a);
        DataKind cb = CanonicalOutputKind(b);
        bool aIsFloat = ca is DataKind.Float32 or DataKind.Float64;
        bool bIsFloat = cb is DataKind.Float32 or DataKind.Float64;
        if (aIsFloat || bIsFloat)
        {
            if (ca == DataKind.Float64 || cb == DataKind.Float64) return DataKind.Float64;
            return DataKind.Float32;
        }
        if (ca == DataKind.Int64 || cb == DataKind.Int64) return DataKind.Int64;
        return DataKind.Int32;
    }

    private static DataValue DataValueFromLong(long value, DataKind kind) => kind switch
    {
        DataKind.Int32 => DataValue.FromInt32((int)value),
        DataKind.Int64 => DataValue.FromInt64(value),
        _ => throw new InvalidOperationException($"Unexpected integer kind: {kind}."),
    };

    private static DataValue DataValueFromDouble(double value, DataKind kind) => kind switch
    {
        DataKind.Float32 => DataValue.FromFloat32((float)value),
        DataKind.Float64 => DataValue.FromFloat64(value),
        _ => throw new InvalidOperationException($"Unexpected float kind: {kind}."),
    };
}
