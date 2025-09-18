using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Applies a lambda function to each element of an array, returning a new array of transformed values.
/// <c>array_transform(array, element -&gt; expression)</c> maps each element through the lambda.
/// Returns null if the input array is null.
/// </summary>
/// <example>
/// <code>array_transform(tags, t -&gt; upper(t))</code> — uppercases each tag.
/// <code>array_transform(prices, p -&gt; p * 1.1)</code> — applies a 10% markup.
/// </example>
public sealed class ArrayTransformFunction : IHigherOrderFunction, IElementKindAwareFunction
{
    private static readonly HashSet<int> LambdaIndices = [1];

    /// <inheritdoc />
    public string Name => "array_transform";

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
            "array_transform() requires a lambda argument and must be called through the higher-order path.");
    }

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
        DataValue[] result = new DataValue[elements.Length];

        DataKind resultElementKind = arrayValue.ArrayElementKind;

        for (int index = 0; index < elements.Length; index++)
        {
            result[index] = lambdaEvaluator(lambda, [elements[index]]);

            // Infer the result element kind from the first non-null result.
            if (index == 0 && !result[index].IsNull)
            {
                resultElementKind = result[index].Kind;
            }
        }

        return DataValue.FromArray(resultElementKind, result);
    }

    private static void ValidateArgumentCount(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
        {
            throw new ArgumentException("array_transform() requires exactly 2 arguments (array, lambda).");
        }

        if (argumentKinds[0] != DataKind.Array)
        {
            throw new ArgumentException(
                $"array_transform() requires an Array as the first argument, got {argumentKinds[0]}.");
        }
    }
}
