using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Returns the current transaction (batch) start time. Equivalent to <c>CURRENT_TIMESTAMP</c>
/// and <c>now()</c> — all three return the same batch-stable value.
/// <c>transaction_timestamp()</c> is named to clearly reflect what it returns.
/// </summary>
/// <remarks>
/// Under normal execution this function is folded to a constant by
/// <see cref="DatumIngest.Execution.TemporalConstantFolder"/> before reaching the evaluator.
/// The <see cref="Execute"/> method is a fallback for direct programmatic API usage.
/// </remarks>
public sealed class TransactionTimestampFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "transaction_timestamp";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 0)
        {
            throw new ArgumentException("transaction_timestamp() takes no arguments.");
        }

        return DataKind.DateTime;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        return DataValue.FromDateTime(DateTimeOffset.UtcNow);
    }
}
