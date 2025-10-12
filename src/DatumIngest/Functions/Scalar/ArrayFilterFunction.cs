using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Filters an array, keeping only elements for which a lambda predicate returns true.
/// <c>array_filter(array, element -&gt; predicate)</c> returns a new array containing
/// only elements where the predicate evaluates to a truthy value.
/// Returns null if the input array is null.
/// </summary>
/// <example>
/// <code>array_filter(scores, s -&gt; s &gt; 0.5)</code> — keeps scores above 0.5.
/// <code>array_filter(tags, t -&gt; t != 'unknown')</code> — removes 'unknown' tags.
/// </example>
public sealed class ArrayFilterFunction : IHigherOrderFunction, IElementKindAwareFunction
{
    private static readonly HashSet<int> LambdaIndices = [1];

    /// <inheritdoc />
    public string Name => "array_filter";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        ValidateArgumentCount(argumentKinds);
        return DataKind.Array;
    }

    /// <inheritdoc />
    public DataKind ValidateArgumentsWithElementKinds(
        ReadOnlySpan<DataKind> argumentKinds,
        ReadOnlySpan<DataKind?> arrayElementKinds)
    {
        ValidateArgumentCount(argumentKinds);
        return DataKind.Array;
    }

    /// <inheritdoc />
    public IReadOnlySet<int> GetLambdaParameterIndices(int argumentCount) => LambdaIndices;

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        throw new InvalidOperationException(
            "array_filter() requires a lambda argument and must be called through the higher-order path.");
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments, IValueStore store) =>
        Execute(arguments);

    /// <inheritdoc />
    public DataValue ExecuteHigherOrder(
        ReadOnlySpan<DataValue> arguments,
        IReadOnlyDictionary<int, LambdaExpression> lambdaArguments,
        LambdaEvaluator lambdaEvaluator)
    {
        DataValue arrayValue = arguments[0];

        if (arrayValue.IsNull)
        {
            return DataValue.NullArray(arrayValue.ArrayElementKind);
        }

        LambdaExpression lambda = lambdaArguments[1];
        DataValue[] elements = arrayValue.AsArray();
        List<DataValue> result = new(elements.Length);

        for (int index = 0; index < elements.Length; index++)
        {
            DataValue predicateResult = lambdaEvaluator(lambda, [elements[index]]);

            if (IsTruthy(predicateResult))
            {
                result.Add(elements[index]);
            }
        }

        return DataValue.FromArray(arrayValue.ArrayElementKind, result.ToArray());
    }

    /// <summary>
    /// Interprets a <see cref="DataValue"/> as a boolean for filter predicate evaluation.
    /// Null is false; zero is false; non-zero numbers and non-empty strings are true.
    /// </summary>
    private static bool IsTruthy(DataValue value)
    {
        if (value.IsNull)
        {
            return false;
        }

        return value.Kind switch
        {
            DataKind.Boolean => value.AsBoolean(),
            DataKind.Float32 => value.AsFloat32() != 0f,
            DataKind.Float64 => value.AsFloat64() != 0.0,
            DataKind.Int32 => value.AsInt32() != 0,
            DataKind.Int64 => value.AsInt64() != 0,
            _ => true,
        };
    }

    private static void ValidateArgumentCount(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
        {
            throw new ArgumentException("array_filter() requires exactly 2 arguments (array, lambda).");
        }

        if (argumentKinds[0] != DataKind.Array)
        {
            throw new ArgumentException(
                $"array_filter() requires an Array as the first argument, got {argumentKinds[0]}.");
        }
    }
}
