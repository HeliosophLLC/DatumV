using DatumIngest.Model;

namespace DatumIngest.Functions.Aggregates;

/// <summary>
/// Implements <c>COUNT(*)</c> (counts all rows) and <c>COUNT(expression)</c>
/// (counts non-null values). The <c>*</c> form receives zero arguments because
/// the parser treats the star as a sentinel that the planner strips.
/// </summary>
public sealed class CountFunction : IAggregateFunction
{
    /// <inheritdoc/>
    public string Name => "COUNT";

    /// <inheritdoc/>
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        // COUNT(*) → 0 args, COUNT(expr) → 1 arg.
        if (argumentKinds.Length > 1)
        {
            throw new ArgumentException("COUNT() accepts zero or one argument.");
        }

        return DataKind.Float32;
    }

    /// <inheritdoc/>
    public IAggregateAccumulator CreateAccumulator() => new CountAccumulator();

    private sealed class CountAccumulator : IAggregateAccumulator
    {
        private long _count;

        public void Accumulate(ReadOnlySpan<DataValue> arguments)
        {
            if (arguments.Length == 0)
            {
                // COUNT(*) — count every row.
                _count++;
            }
            else if (!arguments[0].IsNull)
            {
                // COUNT(expr) — count non-null values.
                _count++;
            }
        }

        /// <inheritdoc/>
        public void Merge(IAggregateAccumulator other)
        {
            CountAccumulator otherAccumulator = (CountAccumulator)other;
            _count += otherAccumulator._count;
        }

        public DataValue Result => DataValue.FromFloat32(_count);

        /// <inheritdoc />
        public void Reset()
        {
            _count = 0;
        }
    }
}
