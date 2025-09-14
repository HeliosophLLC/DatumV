using System.Text.Json;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Extracts a scalar value from a JSON string at a given dot-separated path.
/// <c>json_value(column, jsonPath)</c>
/// Returns String, Float64, Boolean, or null depending on the JSON value type.
/// </summary>
public sealed class JsonValueFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "json_value";

    /// <inheritdoc />
    public int QueryUnitCost => 5;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
        {
            throw new ArgumentException("json_value() requires exactly 2 arguments: json column and path.");
        }

        if (argumentKinds[0] is not (DataKind.JsonValue or DataKind.String))
        {
            throw new ArgumentException($"json_value() first argument must be JsonValue or String, got {argumentKinds[0]}.");
        }

        if (argumentKinds[1] != DataKind.String)
        {
            throw new ArgumentException($"json_value() second argument must be String, got {argumentKinds[1]}.");
        }

        // The actual return kind depends on the JSON value at runtime.
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

        string json = input.Kind == DataKind.JsonValue ? input.AsJsonValue() : input.AsString();
        string path = arguments[1].AsString();

        JsonElement? element = NavigatePath(json, path);
        if (element is null)
        {
            return DataValue.Null(DataKind.String);
        }

        return element.Value.ValueKind switch
        {
            JsonValueKind.String => DataValue.FromString(element.Value.GetString()!),
            JsonValueKind.Number => DataValue.FromFloat64(element.Value.GetDouble()),
            JsonValueKind.True => DataValue.FromBoolean(true),
            JsonValueKind.False => DataValue.FromBoolean(false),
            JsonValueKind.Null => DataValue.Null(DataKind.String),
            // Arrays and objects are not scalar — return null for json_value.
            _ => DataValue.Null(DataKind.String),
        };
    }

    /// <summary>
    /// Navigates a dot-separated path within a JSON document.
    /// Supports paths like "name", "address.city", "items.0.name".
    /// </summary>
    internal static JsonElement? NavigatePath(string json, string path)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            JsonElement current = document.RootElement;
            string[] segments = path.Split('.');

            foreach (string segment in segments)
            {
                if (current.ValueKind == JsonValueKind.Object)
                {
                    if (!current.TryGetProperty(segment, out JsonElement property))
                    {
                        return null;
                    }
                    current = property;
                }
                else if (current.ValueKind == JsonValueKind.Array)
                {
                    if (!int.TryParse(segment, out int arrayIndex) || arrayIndex < 0 || arrayIndex >= current.GetArrayLength())
                    {
                        return null;
                    }
                    current = current[arrayIndex];
                }
                else
                {
                    return null;
                }
            }

            return current.Clone();
        }
    }
}
