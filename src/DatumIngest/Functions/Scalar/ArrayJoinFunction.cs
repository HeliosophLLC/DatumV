using System.Text;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Concatenates array elements into a single string with a separator.
/// <c>array_join(arr, separator)</c> converts each element to its string representation
/// and joins them with the specified separator. Null elements are skipped.
/// Returns null if the array itself is null.
/// </summary>
public sealed class ArrayJoinFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "array_join";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
        {
            throw new ArgumentException("array_join() requires exactly 2 arguments.");
        }

        if (argumentKinds[0] != DataKind.Array)
        {
            throw new ArgumentException(
                $"array_join() requires an Array as the first argument, got {argumentKinds[0]}.");
        }

        if (argumentKinds[1] != DataKind.String)
        {
            throw new ArgumentException(
                $"array_join() requires a String separator as the second argument, got {argumentKinds[1]}.");
        }

        return DataKind.String;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue arrayValue = arguments[0];
        DataValue separatorValue = arguments[1];

        if (arrayValue.IsNull || separatorValue.IsNull)
        {
            return DataValue.Null(DataKind.String);
        }

        DataValue[] elements = arrayValue.AsArray();
        string separator = separatorValue.AsString();

        StringBuilder builder = new();
        bool first = true;

        foreach (DataValue element in elements)
        {
            if (element.IsNull) continue;

            if (!first) builder.Append(separator);
            first = false;

            builder.Append(FormatElement(element));
        }

        return DataValue.FromString(builder.ToString());
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments, IValueStore store)
    {
        DataValue arrayValue = arguments[0];
        DataValue separatorValue = arguments[1];

        if (arrayValue.IsNull || separatorValue.IsNull)
        {
            return DataValue.Null(DataKind.String);
        }

        DataValue[] elements = arrayValue.AsArray();
        string separator = separatorValue.AsString(store);

        StringBuilder builder = new();
        bool first = true;

        foreach (DataValue element in elements)
        {
            if (element.IsNull) continue;

            if (!first) builder.Append(separator);
            first = false;

            builder.Append(FormatElementWithStore(element, store));
        }

        return DataValue.FromString(builder.ToString(), store);
    }

    /// <summary>
    /// Converts an array element to its string representation.
    /// String elements are used directly; other types use their natural ToString form.
    /// </summary>
    private static string FormatElement(DataValue element) =>
        element.Kind == DataKind.String ? element.AsString() : element.ToString();

    /// <summary>
    /// Converts an array element to its string representation using a value store.
    /// String elements are resolved via the store; other types use their natural ToString form.
    /// </summary>
    private static string FormatElementWithStore(DataValue element, IValueStore store) =>
        element.Kind == DataKind.String ? element.AsString(store) : element.ToString();
}
