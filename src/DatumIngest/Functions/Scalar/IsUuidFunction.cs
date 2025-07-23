using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Tests whether a string is a valid UUID.
/// <c>is_uuid(string)</c> — returns Boolean true if the string can be parsed as a UUID.
/// </summary>
public sealed class IsUuidFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "is_uuid";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("is_uuid() requires exactly 1 argument.");
        }

        if (argumentKinds[0] != DataKind.String)
        {
            throw new ArgumentException("is_uuid() argument must be String.");
        }

        return DataKind.Boolean;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];

        if (input.IsNull)
        {
            return DataValue.Null(DataKind.Boolean);
        }

        return DataValue.FromBoolean(Guid.TryParse(input.AsString(), out _));
    }
}
