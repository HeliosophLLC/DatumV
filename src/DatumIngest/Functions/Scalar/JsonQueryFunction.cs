using System.Text.Json;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Extracts a JSON fragment (array or object) from a JSON string at a given dot-separated path.
/// <c>json_query(column, jsonPath)</c>
/// Returns JsonValue for objects/arrays, or Vector if the array contains only numbers.
/// </summary>
public sealed class JsonQueryFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "json_query";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
        {
            throw new ArgumentException("json_query() requires exactly 2 arguments: json column and path.");
        }

        if (argumentKinds[0] is not (DataKind.JsonValue or DataKind.String))
        {
            throw new ArgumentException($"json_query() first argument must be JsonValue or String, got {argumentKinds[0]}.");
        }

        if (argumentKinds[1] != DataKind.String)
        {
            throw new ArgumentException($"json_query() second argument must be String, got {argumentKinds[1]}.");
        }

        return DataKind.JsonValue;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];
        if (input.IsNull)
        {
            return DataValue.Null(DataKind.JsonValue);
        }

        string json = input.Kind == DataKind.JsonValue ? input.AsJsonValue() : input.AsString();
        string path = arguments[1].AsString();

        JsonElement? element = JsonValueFunction.NavigatePath(json, path);
        if (element is null)
        {
            return DataValue.Null(DataKind.JsonValue);
        }

        // If it's a numeric array, return as Vector.
        if (element.Value.ValueKind == JsonValueKind.Array && IsAllNumericArray(element.Value))
        {
            int count = element.Value.GetArrayLength();
            float[] values = new float[count];
            int index = 0;
            foreach (JsonElement item in element.Value.EnumerateArray())
            {
                values[index++] = item.GetSingle();
            }
            return DataValue.FromVector(values);
        }

        // Return as JsonValue for objects, mixed arrays, etc.
        if (element.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            return DataValue.FromJsonValue(element.Value.GetRawText());
        }

        // Scalar values — return as JsonValue text.
        return DataValue.FromJsonValue(element.Value.GetRawText());
    }

    private static bool IsAllNumericArray(JsonElement array)
    {
        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number)
            {
                return false;
            }
        }
        return true;
    }
}
