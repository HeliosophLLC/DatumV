using DatumIngest.Model;

namespace DatumIngest.Functions.Aggregates;

/// <summary>
/// Implements <c>ARRAY_AGG(expression)</c>. Collects all non-null values in the
/// group into a typed array (<see cref="DataValue.Kind"/> = element kind,
/// <see cref="DataValue.IsArray"/> = true). Accepts any single argument of any
/// <see cref="DataKind"/>. The resulting array's element kind matches the
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
    /// <remarks>
    /// ARRAY_AGG produces <c>Array&lt;Scalar&gt;</c>, so the per-element kind
    /// equals the argument kind. Array-ness is signalled via
    /// <see cref="ProducesArray"/>; this method returns the leaf element kind.
    /// </remarks>
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException(
                "ARRAY_AGG() requires exactly one argument.");
        }

        return argumentKinds[0];
    }

    /// <inheritdoc/>
    public bool ProducesArray => true;

    /// <inheritdoc/>
    public IAggregateAccumulator CreateAccumulator() => new ArrayAggregateAccumulator();

    /// <summary>
    /// Collects non-null values into a list, capturing the element kind from
    /// the first non-null value. Returns a typed array (<see cref="DataValue.Kind"/>
    /// = element kind, <see cref="DataValue.IsArray"/> = true) on finalization.
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
        public void Merge(IAggregateAccumulator other, in InvocationFrame frame)
        {
            ArrayAggregateAccumulator otherAccumulator = (ArrayAggregateAccumulator)other;
            _elementKind ??= otherAccumulator._elementKind;
            _elements.AddRange(otherAccumulator._elements);
        }

        public DataValue Result(in InvocationFrame frame)
        {
            if (_elements.Count == 0)
            {
                // Typed null array: Kind = element kind, IsNull + IsArray flags
                // set. Falls back to Float32 when no values were ever observed.
                return DataValue.NullArrayOf(_elementKind ?? DataKind.Float32);
            }

            return DataValue.FromTypedArray(
                _elementKind!.Value,
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_elements),
                frame.Source,
                frame.Target,
                frame.SidecarRegistry);
        }

        /// <inheritdoc />
        public void Reset()
        {
            _elements.Clear();
            _elementKind = null;
        }
    }
}
