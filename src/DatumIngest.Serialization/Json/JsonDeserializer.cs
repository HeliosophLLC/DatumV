using System.Runtime.CompilerServices;
using System.Text.Json;
using DatumIngest.Model;

namespace DatumIngest.Serialization.Json;

/// <summary>
/// Deserializes JSON files containing arrays of objects into <see cref="RowBatch"/>
/// streams. Loads the entire DOM into memory (JSON is not streamable like JSONL).
/// Supports dot-delimited <c>json_path</c> navigation and primitive arrays.
/// Strings are stored via <see cref="SerializationContext.Arena"/>.
/// </summary>
public sealed class JsonDeserializer : IFormatDeserializer
{
    private const int DefaultBatchSize = 1024;
    private const int SchemaSampleSize = 100;

    private readonly FileFormatDescriptor _descriptor;

    /// <summary>Creates a deserializer for the given file descriptor.</summary>
    public JsonDeserializer(FileFormatDescriptor descriptor)
    {
        _descriptor = descriptor;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<RowBatch> DeserializeAsync(
        SerializationContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using Stream stream = await _descriptor.OpenAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        _descriptor.Options.TryGetValue("json_path", out string? jsonPath);
        JsonElement array = JsonNavigator.NavigateToArray(document.RootElement, jsonPath);

        IValueStore store = context.Arena;

        // Primitive array: single "value" column.
        if (JsonNavigator.IsPrimitiveArray(array))
        {
            DataKind kind = JsonNavigator.InferPrimitiveArrayKind(array);
            IReadOnlyList<string> primitiveNames = ["value"];
            Dictionary<string, int> primitiveNameIndex = new(1, StringComparer.OrdinalIgnoreCase) { ["value"] = 0 };

            RowBatch? primitiveBatch = null;
            foreach (JsonElement element in array.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();

                DataValue[] values = context.Pool.RentDataValues(1);
                values[0] = JsonTypeInference.ConvertElement(element, kind, store);

                primitiveBatch ??= context.Pool.RentBatch(DefaultBatchSize);
                primitiveBatch.Add(new Row(primitiveNames, values, primitiveNameIndex));

                if (primitiveBatch.IsFull)
                {
                    yield return primitiveBatch;
                    primitiveBatch = null;
                }
            }

            if (primitiveBatch is not null)
                yield return primitiveBatch;

            yield break;
        }

        // Object array: infer schema, then yield rows.
        (IReadOnlyList<string> names, DataKind[] kinds) = InferSchema(array);

        if (names.Count == 0)
            yield break;

        Dictionary<string, int> nameIndex = new(names.Count, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < names.Count; i++)
            nameIndex[names[i]] = i;

        Dictionary<string, (int Ordinal, DataKind Kind)> columnLookup = new(names.Count, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < names.Count; i++)
            columnLookup[names[i]] = (i, kinds[i]);

        RowBatch? batch = null;

        foreach (JsonElement element in array.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (element.ValueKind != JsonValueKind.Object)
                continue;

            DataValue[] values = context.Pool.RentDataValues(names.Count);

            for (int i = 0; i < names.Count; i++)
                values[i] = DataValue.Null(kinds[i]);

            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (columnLookup.TryGetValue(property.Name, out var col))
                    values[col.Ordinal] = JsonTypeInference.ConvertElement(property.Value, col.Kind, store);
            }

            batch ??= context.Pool.RentBatch(DefaultBatchSize);
            batch.Add(new Row(names, values, nameIndex));

            if (batch.IsFull)
            {
                yield return batch;
                batch = null;
            }
        }

        if (batch is not null)
            yield return batch;
    }

    private static (IReadOnlyList<string> Names, DataKind[] Kinds) InferSchema(JsonElement array)
    {
        Dictionary<string, DataKind> columnKinds = new(StringComparer.OrdinalIgnoreCase);
        List<string> orderedNames = new();
        int sampled = 0;

        foreach (JsonElement element in array.EnumerateArray())
        {
            if (sampled >= SchemaSampleSize) break;
            if (element.ValueKind != JsonValueKind.Object) continue;

            foreach (JsonProperty property in element.EnumerateObject())
            {
                DataKind detected = JsonTypeInference.InferKind(property.Value);

                if (!columnKinds.TryGetValue(property.Name, out DataKind existing))
                {
                    columnKinds[property.Name] = detected;
                    orderedNames.Add(property.Name);
                }
                else
                {
                    columnKinds[property.Name] = JsonTypeInference.WidenKind(existing, detected);
                }
            }

            sampled++;
        }

        string[] names = orderedNames.ToArray();
        DataKind[] kinds = new DataKind[names.Length];
        for (int i = 0; i < names.Length; i++)
            kinds[i] = columnKinds[names[i]];

        return (names, kinds);
    }
}
