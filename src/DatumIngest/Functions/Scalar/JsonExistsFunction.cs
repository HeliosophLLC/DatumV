using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Returns whether a path exists in a JSON string.
/// <c>json_exists(column, jsonPath)</c>
/// Returns Scalar 1.0 if the path exists, 0.0 otherwise.
/// </summary>
public sealed class JsonExistsFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "json_exists";

    /// <inheritdoc />
    public int QueryUnitCost => 5;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
        {
            throw new ArgumentException("json_exists() requires exactly 2 arguments: json column and path.");
        }

        if (argumentKinds[0] is not (DataKind.JsonValue or DataKind.String))
        {
            throw new ArgumentException($"json_exists() first argument must be JsonValue or String, got {argumentKinds[0]}.");
        }

        if (argumentKinds[1] != DataKind.String)
        {
            throw new ArgumentException($"json_exists() second argument must be String, got {argumentKinds[1]}.");
        }

        return DataKind.Boolean;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];
        if (input.IsNull)
        {
            return DataValue.FromBoolean(false);
        }

        string json = input.Kind == DataKind.JsonValue ? input.AsJsonValue() : input.AsString();
        string path = arguments[1].AsString();

        bool exists = JsonValueFunction.NavigatePath(json, path) is not null;
        return DataValue.FromBoolean(exists);
    }
}
