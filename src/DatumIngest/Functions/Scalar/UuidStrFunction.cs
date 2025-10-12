using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Formats a UUID as a lowercase hyphenated string.
/// <c>uuid_str(uuid)</c> — returns the canonical <c>xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx</c> form.
/// </summary>
public sealed class UuidStrFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "uuid_str";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("uuid_str() requires exactly 1 argument.");
        }

        if (argumentKinds[0] != DataKind.Uuid)
        {
            throw new ArgumentException("uuid_str() argument must be Uuid.");
        }

        return DataKind.String;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];

        if (input.IsNull)
        {
            return DataValue.Null(DataKind.String);
        }

        return DataValue.FromString(input.AsUuid().ToString("D"));
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments, IValueStore store)
    {
        DataValue input = arguments[0];

        if (input.IsNull)
        {
            return DataValue.Null(DataKind.String);
        }

        return DataValue.FromString(input.AsUuid().ToString("D"), store);
    }
}
