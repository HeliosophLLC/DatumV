using DatumIngest.Model;

namespace DatumIngest.Functions.Aggregates;

/// <summary>
/// Implements <c>ARRAY_AGG(expression)</c>. Collects all non-null values in the
/// group into a typed <see cref="DataKind.Array"/>. Accepts any single argument
/// of any <see cref="DataKind"/>. The resulting array's element kind matches the
/// argument kind.
/// <para>
/// Supports intra-aggregate <c>ORDER BY</c> to control element order:
/// <c>ARRAY_AGG(name ORDER BY name ASC)</c>. When ORDER BY is specified, the
/// <see cref="Execution.Operators.GroupByOperator"/> sorts buffered rows before
/// calling <see cref="IAggregateAccumulator.Accumulate(ReadOnlySpan{DataValue}, in InvocationFrame)"/>.
/// </para>
/// <para>
/// Returns null if all values are null. Null values are skipped.
/// Supports <c>DISTINCT</c> via <see cref="DistinctAccumulatorDecorator"/>.
/// </para>
/// </summary>
public sealed class ArrayAggregateFunction : IAggregateFunction
{
    /// <inheritdoc/>
    public string Name => "ARRAY_AGG";

    /// <inheritdoc/>
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException(
                "ARRAY_AGG() requires exactly one argument.");
        }

        return DataKind.Array;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Returns the argument kind directly: <c>ARRAY_AGG(scalar_col)</c> produces
    /// <c>Array&lt;Scalar&gt;</c>, so the element kind equals the argument kind.
    /// </remarks>
    public DataKind? GetResultArrayElementKind(ReadOnlySpan<DataKind> argumentKinds) =>
        argumentKinds.Length == 1 ? argumentKinds[0] : null;

    /// <inheritdoc/>
    public IAggregateAccumulator CreateAccumulator() => new ArrayAggregateAccumulator();

    /// <summary>
    /// Collects non-null values into a list, capturing the element kind from the
    /// first non-null value. Returns a typed <see cref="DataKind.Array"/> on finalization.
    /// </summary>
    private sealed class ArrayAggregateAccumulator : IAggregateAccumulator
    {
        private readonly List<DataValue> _elements = [];
        private DataKind? _elementKind;

        public void Accumulate(ReadOnlySpan<DataValue> arguments, in InvocationFrame frame)
        {
            if (arguments[0].IsNull) return;

            _elementKind ??= arguments[0].Kind;
            _elements.Add(arguments[0]);
        }

        /// <inheritdoc/>
        public void Merge(IAggregateAccumulator other)
        {
            ArrayAggregateAccumulator otherAccumulator = (ArrayAggregateAccumulator)other;
            _elementKind ??= otherAccumulator._elementKind;
            _elements.AddRange(otherAccumulator._elements);
        }

        public DataValue Result(in InvocationFrame frame)
        {
            if (_elements.Count == 0)
            {
                return DataValue.NullArray(_elementKind ?? DataKind.Float32);
            }

            return DataValue.FromArray(_elementKind!.Value, _elements, frame.Target);
        }

        /// <inheritdoc />
        public void Reset()
        {
            _elements.Clear();
            _elementKind = null;
        }
    }
}
