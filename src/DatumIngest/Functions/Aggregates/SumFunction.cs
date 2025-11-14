using DatumIngest.Model;

namespace DatumIngest.Functions.Aggregates;

/// <summary>
/// Implements <c>SUM(expression)</c>. Computes the sum of all non-null numeric
/// values. Returns null if all values are null.
/// <para>
/// Output type follows PostgreSQL semantics: integer inputs produce <c>Int64</c>,
/// <c>Float32</c> inputs produce <c>Float32</c>, and <c>Float64</c> inputs
/// produce <c>Float64</c>.
/// </para>
/// </summary>
public sealed class SumFunction : IAggregateFunction
{
    /// <inheritdoc/>
    public string Name => "SUM";

    /// <inheritdoc/>
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("SUM() requires exactly one argument.");
        }

        return argumentKinds[0] switch
        {
            DataKind.Int8 or DataKind.Int16 or DataKind.UInt8 or DataKind.UInt16
                or DataKind.Int32 or DataKind.UInt32 or DataKind.Int64 or DataKind.UInt64
                => DataKind.Int64,
            DataKind.Float32 => DataKind.Float32,
            DataKind.Float64 => DataKind.Float64,
            DataKind other => throw new ArgumentException(
                $"SUM() requires a numeric argument, got {other}."),
        };
    }

    /// <inheritdoc/>
    public IAggregateAccumulator CreateAccumulator() => new SumAccumulator();

    private static long ExtractAsInt64(DataValue value) => value.ToInt64();

    private sealed class SumAccumulator : IAggregateAccumulator
    {
        private long _longSum;
        private double _doubleSum;
        private bool _hasValue;
        private bool _isIntegerKind;
        private bool _isFloat64Kind;

        public void Accumulate(ReadOnlySpan<DataValue> arguments, in InvocationFrame frame)
        {
            DataValue arg = arguments[0];
            if (arg.IsNull) return;

            if (!_hasValue)
            {
                // Capture the accumulation strategy from the first non-null value.
                // The function object is a shared singleton so output-kind detection
                // must live on the per-group accumulator, not on the function.
                _isIntegerKind = arg.Kind is DataKind.Int8 or DataKind.Int16 or DataKind.UInt8
                    or DataKind.UInt16 or DataKind.Int32 or DataKind.UInt32
                    or DataKind.Int64 or DataKind.UInt64;
                _isFloat64Kind = arg.Kind is DataKind.Float64;
                _hasValue = true;
            }

            if (_isIntegerKind)
            {
                _longSum += ExtractAsInt64(arg);
            }
            else
            {
                _doubleSum += _isFloat64Kind ? arg.AsFloat64() : arg.AsFloat32();
            }
        }

        /// <inheritdoc/>
        public void Merge(IAggregateAccumulator other)
        {
            SumAccumulator otherAccumulator = (SumAccumulator)other;
            _longSum += otherAccumulator._longSum;
            _doubleSum += otherAccumulator._doubleSum;

            if (!_hasValue && otherAccumulator._hasValue)
            {
                _isIntegerKind = otherAccumulator._isIntegerKind;
                _isFloat64Kind = otherAccumulator._isFloat64Kind;
            }

            _hasValue |= otherAccumulator._hasValue;
        }

        public DataValue Result(in InvocationFrame frame)
        {
            if (!_hasValue)
            {
                return DataValue.Null(DataKind.Float64);
            }

            if (_isIntegerKind)
            {
                return DataValue.FromInt64(_longSum);
            }

            return _isFloat64Kind
                ? DataValue.FromFloat64(_doubleSum)
                : DataValue.FromFloat32((float)_doubleSum);
        }

        /// <inheritdoc />
        public void Reset()
        {
            _longSum = 0;
            _doubleSum = 0;
            _hasValue = false;
            _isIntegerKind = false;
            _isFloat64Kind = false;
        }
    }
}
