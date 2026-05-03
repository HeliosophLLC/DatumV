using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Aggregates;

/// <summary>
/// Implements <c>ARG_MAX(value, key)</c>. Returns the <c>value</c> from the
/// row where <c>key</c> reaches its maximum. Ties are broken by
/// first-encountered order.
/// <para>
/// The key argument must be a comparable type (numeric, string, date, datetime, time).
/// The value argument may be any type — it is stored, not compared.
/// Returns null if all key values are null.
/// </para>
/// </summary>
public sealed class ArgMaxFunction : IAggregateFunction, IAggregateFunctionMetadata
{
    /// <inheritdoc cref="IAggregateFunctionMetadata.Name"/>
    public static string Name => "ARG_MAX";

    /// <inheritdoc/>
    string IAggregateFunction.Name => Name;

    /// <inheritdoc/>
    public static FunctionCategory Category => FunctionCategory.Aggregate;

    /// <inheritdoc/>
    public static string Description =>
        "Returns the value from the row where key reaches its maximum; ties broken by first-encountered order.";

    /// <inheritdoc/>
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value", DataKindMatcher.Any),
                new ParameterSpec("key",   DataKindMatcher.Any),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.SameAs(0)),
    ];

    /// <inheritdoc/>
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
        {
            throw new ArgumentException($"{Name}() requires exactly two arguments: value and key.");
        }

        DataKind keyKind = argumentKinds[1];
        if (!DataValueComparer.IsComparable(keyKind))
        {
            throw new ArgumentException(
                $"{Name}() second argument (key) must be a comparable type, got {keyKind}.");
        }

        return argumentKinds[0];
    }

    /// <inheritdoc/>
    public IAggregateAccumulator CreateAccumulator() => new ArgMaxAccumulator(findMaximum: true, Name);
}

/// <summary>
/// Implements <c>ARG_MIN(value, key)</c>. Returns the <c>value</c> from the
/// row where <c>key</c> reaches its minimum. Mirror of
/// <see cref="ArgMaxFunction"/>.
/// </summary>
public sealed class ArgMinFunction : IAggregateFunction, IAggregateFunctionMetadata
{
    /// <inheritdoc cref="IAggregateFunctionMetadata.Name"/>
    public static string Name => "ARG_MIN";

    /// <inheritdoc/>
    string IAggregateFunction.Name => Name;

    /// <inheritdoc/>
    public static FunctionCategory Category => FunctionCategory.Aggregate;

    /// <inheritdoc/>
    public static string Description =>
        "Returns the value from the row where key reaches its minimum; ties broken by first-encountered order.";

    /// <inheritdoc/>
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value", DataKindMatcher.Any),
                new ParameterSpec("key",   DataKindMatcher.Any),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.SameAs(0)),
    ];

    /// <inheritdoc/>
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
        {
            throw new ArgumentException($"{Name}() requires exactly two arguments: value and key.");
        }

        DataKind keyKind = argumentKinds[1];
        if (!DataValueComparer.IsComparable(keyKind))
        {
            throw new ArgumentException(
                $"{Name}() second argument (key) must be a comparable type, got {keyKind}.");
        }

        return argumentKinds[0];
    }

    /// <inheritdoc/>
    public IAggregateAccumulator CreateAccumulator() => new ArgMaxAccumulator(findMaximum: false, Name);
}

/// <summary>
/// Accumulator that tracks the key extremum and the associated value. Shared
/// between <see cref="ArgMaxFunction"/> and <see cref="ArgMinFunction"/>.
/// </summary>
internal sealed class ArgMaxAccumulator : IAggregateAccumulator
{
    private readonly bool _findMaximum;
    private readonly string _functionName;
    private DataValue _bestValue;
    private DataValue _bestKey;
    private bool _hasValue;
    private DataKind _valueKind = DataKind.Float64;

    internal ArgMaxAccumulator(bool findMaximum, string functionName)
    {
        _findMaximum = findMaximum;
        _functionName = functionName;
    }

    /// <inheritdoc/>
    public void Accumulate(ReadOnlySpan<DataValue> arguments, in InvocationFrame frame)
    {
        DataValue value = arguments[0];
        DataValue key = arguments[1];

        if (key.IsNull) return;

        _valueKind = value.Kind;

        if (!_hasValue || IsBetter(key, frame.Source, _bestKey, frame.Target))
        {
            _bestValue = DataValueRetention.Stabilize(value, frame.Source, frame.Target);
            _bestKey = DataValueRetention.Stabilize(key, frame.Source, frame.Target);
            _hasValue = true;
        }
    }

    /// <inheritdoc/>
    public ValueTask MergeAsync(IAggregateAccumulator other, InvocationFrame frame)
    {
        ArgMaxAccumulator otherAccumulator = (ArgMaxAccumulator)other;

        if (!otherAccumulator._hasValue)
            return ValueTask.CompletedTask;

        if (!_hasValue || IsBetter(otherAccumulator._bestKey, frame.Target, _bestKey, frame.Target))
        {
            _bestValue = otherAccumulator._bestValue;
            _bestKey = otherAccumulator._bestKey;
            _valueKind = otherAccumulator._valueKind;
            _hasValue = true;
        }
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask<DataValue> ResultAsync(InvocationFrame frame)
    {
        if (!_hasValue) return new(DataValue.Null(_valueKind));
        return new(DataValueRetention.Stabilize(_bestValue, frame.Source, frame.Target));
    }

    /// <inheritdoc/>
    public void Reset()
    {
        _bestValue = default;
        _bestKey = default;
        _hasValue = false;
        _valueKind = DataKind.Float64;
    }

    private bool IsBetter(
        DataValue candidate, IValueStore candidateStore,
        DataValue current, IValueStore currentStore)
    {
        int comparison = DataValueComparer.Compare(candidate, candidateStore, current, currentStore);
        return _findMaximum ? comparison > 0 : comparison < 0;
    }
}
