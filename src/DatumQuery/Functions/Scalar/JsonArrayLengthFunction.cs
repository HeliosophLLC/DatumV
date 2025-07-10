using System.Text.Json;
using Axon.QueryEngine.Model;

namespace Axon.QueryEngine.Functions.Scalar;

/// <summary>
/// Returns the number of elements in a JSON array.
/// <c>json_array_length(column)</c> for root array.
/// <c>json_array_length(column, jsonPath)</c> for nested array at path.
/// </summary>
public sealed class JsonArrayLengthFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "json_array_length";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length < 1 || argumentKinds.Length > 2)
        {
            throw new ArgumentException("json_array_length() requires 1 or 2 arguments: json column, [path].");
        }

        if (argumentKinds[0] is not (DataKind.JsonValue or DataKind.String))
        {
            throw new ArgumentException($"json_array_length() first argument must be JsonValue or String, got {argumentKinds[0]}.");
        }

        if (argumentKinds.Length == 2 && argumentKinds[1] != DataKind.String)
        {
            throw new ArgumentException($"json_array_length() second argument must be String, got {argumentKinds[1]}.");
        }

        return DataKind.Scalar;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];
        if (input.IsNull)
        {
            return DataValue.Null(DataKind.Scalar);
        }

        string json = input.Kind == DataKind.JsonValue ? input.AsJsonValue() : input.AsString();

        JsonElement? element;
        if (arguments.Length == 2)
        {
            string path = arguments[1].AsString();
            element = JsonValueFunction.NavigatePath(json, path);
        }
        else
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(json);
                element = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                return DataValue.Null(DataKind.Scalar);
            }
        }

        if (element is null || element.Value.ValueKind != JsonValueKind.Array)
        {
            return DataValue.Null(DataKind.Scalar);
        }

        return DataValue.FromScalar(element.Value.GetArrayLength());
    }
}
