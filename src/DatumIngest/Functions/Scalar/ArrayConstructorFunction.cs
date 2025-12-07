using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Constructs a typed array from zero or more homogeneous arguments.
/// <c>array(a, b, c)</c> requires all arguments to share the same
/// <see cref="DataKind"/>. Null arguments are preserved as null elements.
/// Zero arguments returns an empty <c>String[]</c>.
/// </summary>
public sealed class ArrayConstructorFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "array";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Utility;

    /// <inheritdoc />
    public static string Description =>
        "Constructs a typed array from zero or more homogeneous arguments.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [],
            VariadicTrailing: new VariadicSpec("elements", DataKindMatcher.Any, MinOccurrences: 0),
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Custom(
                kinds => kinds.Length == 0 ? DataKind.String : kinds[0],
                "same as element kind (String when empty)"))),
    ];

    /// <inheritdoc />
    public bool ProducesArray => true;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length == 0)
            return DataKind.String;

        DataKind elementKind = argumentKinds[0];
        for (int i = 1; i < argumentKinds.Length; i++)
        {
            if (argumentKinds[i] != elementKind)
            {
                throw new FunctionArgumentException(Name,
                    $"all arguments must have the same type; " +
                    $"argument 1 is {elementKind} but argument {i + 1} is {argumentKinds[i]}.");
            }
        }
        return elementKind;
    }

    /// <inheritdoc />
    public ValueRef Execute(ReadOnlySpan<ValueRef> arguments, in EvaluationFrame frame)
    {
        if (arguments.Length == 0)
            return ValueRef.FromArray(DataKind.String, []);

        DataKind elementKind = arguments[0].Kind;
        ValueRef[] elements = new ValueRef[arguments.Length];
        for (int i = 0; i < arguments.Length; i++)
            elements[i] = arguments[i];
        return ValueRef.FromArray(elementKind, elements);
    }
}
