using DatumIngest.Manifest;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;
using DatumIngest.Model;

namespace DatumIngest.Functions.TableValued;

/// <summary>
/// Generates a sequence of rows with a single <c>Value</c> column ranging from
/// <c>start</c> to <c>end</c> (inclusive) with an optional <c>step</c> (default 1).
/// The output kind matches the widest numeric kind among the arguments:
/// Int32 &lt; Int64 &lt; Float32 &lt; Float64.
/// </summary>
/// <remarks>
/// Usage: <c>FROM RANGE(0, 360)</c> or <c>FROM RANGE(0.0, 1.0, 0.1) AS r</c>.
/// </remarks>
public sealed class RangeFunction : ITableValuedFunctionMetadata, ITableValuedFunction
{
    private static readonly ColumnLookup ValueColumnLookup = new(["Value"]);

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Name => "range";

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static FunctionCategory Category => FunctionCategory.Table;

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Description =>
        "Generates a sequence of rows with a single Value column: RANGE(start, end[, step]). " +
        "The output type matches the widest numeric type among the arguments.";

    string ITableValuedFunction.Name => Name;

    /// <inheritdoc />
    public Schema ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length is not (2 or 3))
        {
            throw new FunctionArgumentException(Name,
                "requires 2 or 3 arguments: range(start, end[, step]).");
        }

        DataKind outputKind = DataKind.Int32;
        for (int i = 0; i < argumentKinds.Length; i++)
        {
            if (!DataValueComparer.IsNumericScalar(argumentKinds[i]))
            {
                throw new FunctionArgumentException(Name,
                    $"argument {i + 1} must be a numeric type (Int32, Int64, Float32, or Float64).");
            }
            outputKind = PromoteKind(outputKind, argumentKinds[i]);
        }

        return new Schema([new ColumnInfo("Value", outputKind, nullable: false)]);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RowBatch> ExecuteAsync(ValueRef[] arguments, ExecutionContext context)
    {
        if (arguments.Length is not (2 or 3))
        {
            throw new ArgumentException("range() requires 2 or 3 arguments: range(start, end[, step]).");
        }

        DataKind outputKind = DataKind.Int32;
        for (int i = 0; i < arguments.Length; i++)
        {
            outputKind = PromoteKind(outputKind, arguments[i].Kind);
        }

        CancellationToken cancellationToken = context.CancellationToken;
        RowBatch? batch = null;

        if (IsIntegerKind(outputKind))
        {
            long start = ToInt64(arguments[0]);
            long end = ToInt64(arguments[1]);
            long step = arguments.Length == 3 ? ToInt64(arguments[2]) : 1L;

            ValidateIntStep(step, start, end);

            for (long current = start; step > 0 ? current <= end : current >= end; current += step)
            {
                cancellationToken.ThrowIfCancellationRequested();
                batch ??= context.RentRowBatch(ValueColumnLookup);
                batch.Add([DataValueFromLong(current, outputKind)]);
                if (batch.IsFull)
                {
                    yield return batch;
                    batch = null;
                }
            }
        }
        else
        {
            double start = ToFloat64(arguments[0]);
            double end = ToFloat64(arguments[1]);
            double step = arguments.Length == 3 ? ToFloat64(arguments[2]) : 1.0;

            ValidateFloatStep(step, start, end);

            // Use integer counter to avoid floating-point drift accumulation.
            double tolerance = System.Math.Abs(step) * 1e-9;
            for (int i = 0; ; i++)
            {
                double current = start + i * step;
                if (step > 0 ? current > end + tolerance : current < end - tolerance)
                {
                    break;
                }

                cancellationToken.ThrowIfCancellationRequested();
                batch ??= context.RentRowBatch(ValueColumnLookup);
                batch.Add([DataValueFromDouble(current, outputKind)]);
                if (batch.IsFull)
                {
                    yield return batch;
                    batch = null;
                }
            }
        }

        if (batch is not null)
        {
            yield return batch;
        }

        await Task.CompletedTask;
    }

    private static void ValidateIntStep(long step, long start, long end)
    {
        if (step == 0L)
        {
            throw new ArgumentException("range() step cannot be zero.");
        }
        if (step > 0 && start > end)
        {
            throw new ArgumentException("range() step must be negative when start > end.");
        }
        if (step < 0 && start < end)
        {
            throw new ArgumentException("range() step must be positive when start < end.");
        }
    }

    private static void ValidateFloatStep(double step, double start, double end)
    {
        if (step == 0.0)
        {
            throw new ArgumentException("range() step cannot be zero.");
        }
        if (step > 0 && start > end)
        {
            throw new ArgumentException("range() step must be negative when start > end.");
        }
        if (step < 0 && start < end)
        {
            throw new ArgumentException("range() step must be positive when start < end.");
        }
    }

    // Only used after PromoteKind has canonicalized the output kind to Int32/Int64/Float32/Float64.
    private static bool IsIntegerKind(DataKind kind) =>
        kind is DataKind.Int32 or DataKind.Int64;

    // Maps any numeric kind to the canonical output kind used for iteration and output.
    // Small and unsigned integers promote to Int32 or Int64; float kinds stay as-is.
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
        // Float always wins over integer.
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

    // ValueRef.ToInt64() coerces any numeric kind to long.
    private static long ToInt64(ValueRef v) => v.ToInt64();

    // ValueRef.ToDouble() coerces any numeric kind to double.
    private static double ToFloat64(ValueRef v) => v.ToDouble();

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
