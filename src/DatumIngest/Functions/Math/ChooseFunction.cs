using DatumIngest.Model;

namespace DatumIngest.Functions.Math;

/// <summary>
/// Selects a value from a list by 1-based index: <c>choose(index, val_1, val_2, ..., val_n)</c>.
/// Returns null if the index is null, less than 1, or greater than the number of values.
/// </summary>
public sealed class ChooseFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "choose";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length < 2)
        {
            throw new ArgumentException("choose() requires at least 2 arguments: index and one or more values.");
        }

        if (!DataValueComparer.IsNumericScalar(argumentKinds[0]))
        {
            throw new ArgumentException(
                $"choose() first argument (index) must be numeric, got {argumentKinds[0]}.");
        }

        DataKind valueKind = argumentKinds[1];
        for (int i = 2; i < argumentKinds.Length; i++)
        {
            if (argumentKinds[i] != valueKind)
            {
                throw new ArgumentException(
                    $"choose() all value arguments must be {valueKind}, but argument {i + 1} is {argumentKinds[i]}.");
            }
        }

        return valueKind;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue indexArgument = arguments[0];
        if (indexArgument.IsNull)
        {
            return DataValue.Null(arguments[1].Kind);
        }

        int index = indexArgument.ToInt32();

        // 1-based: valid range is [1, arguments.Length - 1].
        if (index < 1 || index > arguments.Length - 1)
        {
            return DataValue.Null(arguments[1].Kind);
        }

        return arguments[index];
    }
}
