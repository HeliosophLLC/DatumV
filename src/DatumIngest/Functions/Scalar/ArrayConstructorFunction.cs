using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Constructs a typed <see cref="DataKind.Array"/> from one or more arguments.
/// <c>array(a, b, c)</c> accepts a variable number of arguments that must all share
/// the same <see cref="DataKind"/>. Returns a single <see cref="DataValue"/> of kind
/// <see cref="DataKind.Array"/> with element kind matching the argument kind.
/// Null arguments are preserved as null elements in the resulting array.
/// </summary>
public sealed class ArrayConstructorFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "array";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length < 1)
        {
            throw new ArgumentException("array() requires at least 1 argument.");
        }

        DataKind elementKind = argumentKinds[0];
        for (int i = 1; i < argumentKinds.Length; i++)
        {
            if (argumentKinds[i] != elementKind)
            {
                throw new ArgumentException(
                    $"array() requires all arguments to have the same type. " +
                    $"Argument 1 is {elementKind} but argument {i + 1} is {argumentKinds[i]}.");
            }
        }

        return DataKind.Array;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataKind elementKind = arguments[0].Kind;

        DataValue[] elements = new DataValue[arguments.Length];
        for (int i = 0; i < arguments.Length; i++)
        {
            elements[i] = arguments[i];
        }

        return DataValue.FromArray(elementKind, elements);
    }
}
