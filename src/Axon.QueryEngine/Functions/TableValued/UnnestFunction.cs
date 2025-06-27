using System.Runtime.CompilerServices;
using System.Text.Json;
using Axon.QueryEngine.Model;

namespace Axon.QueryEngine.Functions.TableValued;

/// <summary>
/// Takes an array-valued column (Vector, UInt8Array, or JsonValue array) and
/// expands each element into a separate row.
/// When unnesting a JsonValue array of objects (e.g. from zip()), each object
/// property becomes a named column.
/// </summary>
public sealed class UnnestFunction : ITableValuedFunction
{
    /// <inheritdoc />
    public string Name => "unnest";

    /// <inheritdoc />
    public async IAsyncEnumerable<Row> ExecuteAsync(
        DataValue[] arguments,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (arguments.Length != 1)
        {
            throw new ArgumentException("unnest() requires exactly 1 argument.");
        }

        DataValue input = arguments[0];
        if (input.IsNull)
        {
            yield break;
        }

        switch (input.Kind)
        {
            case DataKind.Vector:
            {
                float[] values = input.AsVector();
                string[] names = ["value"];
                foreach (float item in values)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return new Row(names, [DataValue.FromScalar(item)]);
                }
                break;
            }

            case DataKind.UInt8Array:
            {
                byte[] bytes = input.AsUInt8Array();
                string[] names = ["value"];
                foreach (byte item in bytes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return new Row(names, [DataValue.FromUInt8(item)]);
                }
                break;
            }

            case DataKind.JsonValue:
            {
                string jsonText = input.AsJsonValue();
                using JsonDocument document = JsonDocument.Parse(jsonText);
                JsonElement root = document.RootElement;

                if (root.ValueKind != JsonValueKind.Array)
                {
                    throw new ArgumentException("unnest() JSON argument must be an array.");
                }

                foreach (JsonElement item in root.EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (item.ValueKind == JsonValueKind.Object)
                    {
                        // Expand object properties into named columns.
                        int propertyCount = 0;
                        foreach (JsonProperty _ in item.EnumerateObject())
                        {
                            propertyCount++;
                        }

                        string[] names = new string[propertyCount];
                        DataValue[] values = new DataValue[propertyCount];
                        int index = 0;
                        foreach (JsonProperty property in item.EnumerateObject())
                        {
                            names[index] = property.Name;
                            values[index] = ConvertJsonElement(property.Value);
                            index++;
                        }
                        yield return new Row(names, values);
                    }
                    else
                    {
                        // Scalar elements get a single "value" column.
                        yield return new Row(["value"], [ConvertJsonElement(item)]);
                    }
                }
                break;
            }

            default:
                throw new ArgumentException($"unnest() does not support {input.Kind}.");
        }

        // Ensure the method is truly async to satisfy the compiler.
        await Task.CompletedTask;
    }

    private static DataValue ConvertJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => DataValue.FromString(element.GetString()!),
            JsonValueKind.Number => DataValue.FromScalar(element.GetSingle()),
            JsonValueKind.True => DataValue.FromScalar(1.0f),
            JsonValueKind.False => DataValue.FromScalar(0.0f),
            JsonValueKind.Null => DataValue.Null(DataKind.String),
            JsonValueKind.Array or JsonValueKind.Object => DataValue.FromJsonValue(element.GetRawText()),
            _ => DataValue.Null(DataKind.String),
        };
    }
}
