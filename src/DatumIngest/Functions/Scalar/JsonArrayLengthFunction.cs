using System.Text.Json;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

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
    public int QueryUnitCost => 5;

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

        return DataKind.Int32;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];
        if (input.IsNull)
        {
            return DataValue.Null(DataKind.Int32);
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
                return DataValue.Null(DataKind.Int32);
            }
        }

        if (element is null || element.Value.ValueKind != JsonValueKind.Array)
        {
            return DataValue.Null(DataKind.Int32);
        }

        return DataValue.FromInt32(element.Value.GetArrayLength());
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments, IValueStore store)
    {
        DataValue input = arguments[0];
        if (input.IsNull)
        {
            return DataValue.Null(DataKind.Int32);
        }

        ReadOnlySpan<byte> utf8 = input.AsUtf8Span(store);

        JsonElement? element;
        if (arguments.Length == 2)
        {
            string path = arguments[1].AsString(store);
            element = JsonValueFunction.NavigatePathUtf8(utf8, path);
        }
        else
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(new ReadOnlyMemory<byte>(utf8.ToArray()));
                element = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                return DataValue.Null(DataKind.Int32);
            }
        }

        if (element is null || element.Value.ValueKind != JsonValueKind.Array)
        {
            return DataValue.Null(DataKind.Int32);
        }

        return DataValue.FromInt32(element.Value.GetArrayLength());
    }
}
