using System.Text;
using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Joins a <c>String[]</c> into a single string using the specified delimiter.
/// Null elements are skipped unless a null-replacement string is supplied.
/// A null array or null delimiter yields a null result.
/// </summary>
/// <remarks>
/// <para>
/// <c>array_to_string(array, delimiter)</c> — null elements are silently skipped.
/// <c>array_to_string(array, delimiter, null_string)</c> — null elements are
/// replaced by <c>null_string</c> before joining.
/// </para>
/// </remarks>
public sealed class ArrayToStringFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "array_to_string";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.String;

    /// <inheritdoc />
    public static string Description =>
        "Joins a String array into a single string using the specified delimiter. " +
        "Null elements are skipped unless a null_string replacement is provided.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("array", DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("delimiter", DataKindMatcher.Exact(DataKind.String)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.String)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("array", DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("delimiter", DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("null_string", DataKindMatcher.Exact(DataKind.String)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.String)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ArrayToStringFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueRef Execute(ReadOnlySpan<ValueRef> arguments, in EvaluationFrame frame)
    {
        ValueRef arrayArg = arguments[0];
        ValueRef delimiterArg = arguments[1];

        if (!arrayArg.IsArray)
            throw new FunctionArgumentException(Name, "first argument must be a String array.");

        if (arrayArg.IsNull || delimiterArg.IsNull)
            return ValueRef.Null(DataKind.String);

        string delimiter = delimiterArg.AsString();
        string? nullReplacement = arguments.Length >= 3 && !arguments[2].IsNull
            ? arguments[2].AsString()
            : null;

        ReadOnlySpan<ValueRef> elements = arrayArg.GetArrayElements();
        StringBuilder sb = new();
        bool first = true;

        foreach (ValueRef element in elements)
        {
            string? value;
            if (element.IsNull)
            {
                if (nullReplacement is null) continue;
                value = nullReplacement;
            }
            else
            {
                value = element.AsString();
            }

            if (!first) sb.Append(delimiter);
            sb.Append(value);
            first = false;
        }

        return ValueRef.FromString(sb.ToString());
    }
}
