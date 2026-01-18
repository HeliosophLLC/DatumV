using DatumIngest.Model;

namespace DatumIngest.Functions.Aggregates;

/// <summary>
/// Implements <c>STRING_AGG(expression, separator)</c>. Concatenates all
/// non-null string values in the group, separated by the given separator.
/// Supports intra-aggregate <c>ORDER BY</c> to control concatenation order:
/// <c>STRING_AGG(name, ', ' ORDER BY name ASC)</c>.
/// <para>
/// Arguments: first is the expression to concatenate (must be String),
/// second is the separator (must be String, constant across all rows —
/// the value from the first accumulated row is used).
/// </para>
/// <para>
/// Returns null if all values are null. Null values are skipped.
/// When ORDER BY is specified, the GroupByOperator
/// sorts buffered rows before calling <see cref="IAggregateAccumulator.Accumulate(ReadOnlySpan{DataValue}, in InvocationFrame)"/>.
/// </para>
/// </summary>
public sealed class StringAggregateFunction : IAggregateFunction
{
    /// <inheritdoc/>
    public string Name => "STRING_AGG";

    /// <inheritdoc/>
    // O(N) string collection and join at finalization — Tier 2.
    public int QueryUnitCost => 2;

    /// <inheritdoc/>
    public WithinGroupSemantics WithinGroupSemantics => WithinGroupSemantics.SortModifier;

    /// <inheritdoc/>
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
        {
            throw new ArgumentException(
                "STRING_AGG() requires exactly two arguments: expression and separator.");
        }

        if (argumentKinds[0] is not DataKind.String)
        {
            throw new ArgumentException(
                $"STRING_AGG() first argument must be String, got {argumentKinds[0]}.");
        }

        if (argumentKinds[1] is not DataKind.String)
        {
            throw new ArgumentException(
                $"STRING_AGG() second argument (separator) must be String, got {argumentKinds[1]}.");
        }

        return DataKind.String;
    }

    /// <inheritdoc/>
    public IAggregateAccumulator CreateAccumulator() => new StringAggregateAccumulator();

    /// <summary>
    /// Collects non-null string values and joins them with the captured separator
    /// at finalization. The separator is captured from the first accumulated row.
    /// </summary>
    private sealed class StringAggregateAccumulator : IAggregateAccumulator
    {
        private readonly List<string> _values = [];
        private string _separator = "";
        private bool _separatorCaptured;

        public void Accumulate(ReadOnlySpan<DataValue> arguments, in InvocationFrame frame)
        {
            if (!_separatorCaptured && !arguments[1].IsNull)
            {
                _separator = arguments[1].AsString(frame.Source);
                _separatorCaptured = true;
            }

            if (arguments[0].IsNull) return;

            _values.Add(arguments[0].AsString(frame.Source));
        }

        /// <inheritdoc/>
        public void Merge(IAggregateAccumulator other, in InvocationFrame frame)
        {
            StringAggregateAccumulator otherAccumulator = (StringAggregateAccumulator)other;
            _values.AddRange(otherAccumulator._values);

            if (!_separatorCaptured && otherAccumulator._separatorCaptured)
            {
                _separator = otherAccumulator._separator;
                _separatorCaptured = true;
            }
        }

        public DataValue Result(in InvocationFrame frame)
        {
            if (_values.Count == 0)
            {
                return DataValue.Null(DataKind.String);
            }

            return DataValue.FromString(string.Join(_separator, _values), frame.Target);
        }

        /// <inheritdoc />
        public void Reset()
        {
            _values.Clear();
            _separator = "";
            _separatorCaptured = false;
        }
    }
}
