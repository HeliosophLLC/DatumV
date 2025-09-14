using System.Runtime.CompilerServices;
using System.Text.Json;
using DatumIngest.Model;

namespace DatumIngest.Functions.TableValued;

/// <summary>
/// Takes an array-valued column (Vector, UInt8Array, or JsonValue array) and
/// expands each element into a separate row.
/// When unnesting a JsonValue array of objects (e.g. from zip()), each object
/// property becomes a named column.
/// </summary>
public sealed class UnnestFunction : IElementKindAwareTableFunction
{
    /// <summary>
    /// Number of rows accumulated before yielding a batch.
    /// </summary>
    private const int DefaultBatchSize = 1024;

    /// <inheritdoc />
    public string Name => "unnest";

    /// <inheritdoc />
    public Schema GetOutputSchema(ReadOnlySpan<DataKind> argumentKinds) =>
        GetOutputSchema(argumentKinds, []);

    /// <inheritdoc />
    /// <remarks>
    /// When <paramref name="argumentKinds"/>[0] is <see cref="DataKind.Array"/> and
    /// <paramref name="arrayElementKinds"/>[0] is known, the output column uses the
    /// element kind directly. Without element kind metadata the fallback is String,
    /// matching the existing JSON-array behaviour.
    /// </remarks>
    public Schema GetOutputSchema(ReadOnlySpan<DataKind> argumentKinds, ReadOnlySpan<DataKind?> arrayElementKinds)
    {
        if (argumentKinds.Length != 1)
        {
            return new Schema([new ColumnInfo("value", DataKind.Float32, nullable: true)]);
        }

        DataKind inputKind = argumentKinds[0];
        DataKind? elementKind = arrayElementKinds.Length > 0 ? arrayElementKinds[0] : null;

        return inputKind switch
        {
            // Vector elements are floats.
            DataKind.Vector => new Schema(
                [new ColumnInfo("value", DataKind.Float32, nullable: false)]),

            // UInt8Array elements are bytes.
            DataKind.UInt8Array => new Schema(
                [new ColumnInfo("value", DataKind.UInt8, nullable: false)]),

            // Use the element kind when it is known at plan time; otherwise fall
            // back to String, matching the existing JSON-array behaviour.
            DataKind.Array => elementKind is not null
                ? new Schema([new ColumnInfo("value", elementKind.Value, nullable: true)])
                : new Schema([new ColumnInfo("value", DataKind.String, nullable: true)]),

            // JSON arrays may expand to objects with unknown columns;
            // fall back to a single "value" column of String kind.
            _ => new Schema(
                [new ColumnInfo("value", DataKind.String, nullable: true)]),
        };
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RowBatch> ExecuteAsync(
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

        RowBatch? batch = null;

        switch (input.Kind)
        {
            case DataKind.Vector:
            {
                float[] values = input.AsVector();
                string[] names = ["value"];
                Dictionary<string, int> nameIndex = new(1, StringComparer.OrdinalIgnoreCase) { ["value"] = 0 };
                foreach (float item in values)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    batch ??= RowBatch.Rent(DefaultBatchSize);
                    batch.Add(new Row(names, [DataValue.FromFloat32(item)], nameIndex));
                    if (batch.IsFull)
                    {
                        yield return batch;
                        batch = null;
                    }
                }
                break;
            }

            case DataKind.UInt8Array:
            {
                byte[] bytes = input.AsUInt8Array();
                string[] names = ["value"];
                Dictionary<string, int> nameIndex = new(1, StringComparer.OrdinalIgnoreCase) { ["value"] = 0 };
                foreach (byte item in bytes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    batch ??= RowBatch.Rent(DefaultBatchSize);
                    batch.Add(new Row(names, [DataValue.FromUInt8(item)], nameIndex));
                    if (batch.IsFull)
                    {
                        yield return batch;
                        batch = null;
                    }
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
                        batch ??= RowBatch.Rent(DefaultBatchSize);
                        batch.Add(new Row(names, values));
                        if (batch.IsFull)
                        {
                            yield return batch;
                            batch = null;
                        }
                    }
                    else
                    {
                        // Scalar elements get a single "value" column.
                        batch ??= RowBatch.Rent(DefaultBatchSize);
                        batch.Add(new Row(["value"], [ConvertJsonElement(item)]));
                        if (batch.IsFull)
                        {
                            yield return batch;
                            batch = null;
                        }
                    }
                }
                break;
            }

            case DataKind.Array:
            {
                DataValue[] elements = input.AsArray();
                string[] names = ["value"];
                Dictionary<string, int> nameIndex = new(1, StringComparer.OrdinalIgnoreCase) { ["value"] = 0 };
                foreach (DataValue element in elements)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    batch ??= RowBatch.Rent(DefaultBatchSize);
                    batch.Add(new Row(names, [element], nameIndex));
                    if (batch.IsFull)
                    {
                        yield return batch;
                        batch = null;
                    }
                }
                break;
            }

            default:
                throw new ArgumentException($"unnest() does not support {input.Kind}.");
        }

        if (batch is not null)
        {
            yield return batch;
        }

        // Ensure the method is truly async to satisfy the compiler.
        await Task.CompletedTask;
    }

    private static DataValue ConvertJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => DataValue.FromString(element.GetString()!),
            JsonValueKind.Number => DataValue.FromFloat64(element.GetDouble()),
            JsonValueKind.True => DataValue.FromBoolean(true),
            JsonValueKind.False => DataValue.FromBoolean(false),
            JsonValueKind.Null => DataValue.Null(DataKind.String),
            JsonValueKind.Array or JsonValueKind.Object => DataValue.FromJsonValue(element.GetRawText()),
            _ => DataValue.Null(DataKind.String),
        };
    }
}
