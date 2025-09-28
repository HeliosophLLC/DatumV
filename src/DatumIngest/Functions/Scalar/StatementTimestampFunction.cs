using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Returns the start time of the current statement. Within a batch,
/// <c>statement_timestamp()</c> and <c>transaction_timestamp()</c> return the same
/// value for the first statement, but may differ for subsequent statements.
/// </summary>
/// <remarks>
/// Under normal execution this function is folded to a constant by
/// <see cref="DatumIngest.Execution.TemporalConstantFolder"/> before reaching the evaluator.
/// The <see cref="Execute"/> method is a fallback for direct programmatic API usage.
/// </remarks>
public sealed class StatementTimestampFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "statement_timestamp";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 0)
        {
            throw new ArgumentException("statement_timestamp() takes no arguments.");
        }

        return DataKind.DateTime;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        return DataValue.FromDateTime(DateTimeOffset.UtcNow);
    }
}
