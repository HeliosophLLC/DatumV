using System.Runtime.CompilerServices;
using System.Text.Json;
using DatumIngest.Model;
using DatumIngest.Serialization.Json;

namespace DatumIngest.Serialization.Jsonl;

/// <summary>
/// Deserializes newline-delimited JSON (JSONL/NDJSON) files into <see cref="RowBatch"/>
/// streams. Streams line-by-line for constant memory usage. Schema is inferred from the
/// first 100 lines. Strings are stored via <see cref="SerializationContext.Arena"/>.
/// </summary>
public sealed class JsonlDeserializer : IFormatDeserializer
{
    private const int SchemaSampleSize = 100;
    private const int DefaultBatchSize = 1024;

    private readonly FileFormatDescriptor _descriptor;

    /// <summary>Creates a deserializer for the given file descriptor.</summary>
    public JsonlDeserializer(FileFormatDescriptor descriptor)
    {
        _descriptor = descriptor;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<RowBatch> DeserializeAsync(
        SerializationContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using Stream stream = await _descriptor.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Pass 1: infer schema from first N lines.
        (IReadOnlyList<string> names, DataKind[] kinds) = InferSchema(stream);
        stream.Seek(0, SeekOrigin.Begin);

        if (names.Count == 0)
            yield break;

        Dictionary<string, int> nameIndex = new(names.Count, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < names.Count; i++)
            nameIndex[names[i]] = i;

        // Build column lookup for fast property → ordinal resolution.
        Dictionary<string, (int Ordinal, DataKind Kind)> columnLookup = new(names.Count, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < names.Count; i++)
            columnLookup[names[i]] = (i, kinds[i]);

        // Pass 2: stream rows.
        IValueStore store = context.Arena;
        using StreamReader reader = new(stream, leaveOpen: true);
        RowBatch? batch = null;
        int lineNumber = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string? line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) break;
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException ex)
            {
                throw new DeserializationException(
                    $"Malformed JSON at line {lineNumber}: {ex.Message}", lineNumber, ex);
            }

            using (document)
            {
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    throw new DeserializationException(
                        $"Expected JSON object at line {lineNumber}, got {root.ValueKind}.", lineNumber);

                DataValue[] values = context.Pool.RentDataValues(names.Count);

                for (int i = 0; i < names.Count; i++)
                    values[i] = DataValue.Null(kinds[i]);

                foreach (JsonProperty property in root.EnumerateObject())
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
        }

        if (batch is not null)
        {
            yield return batch;
        }
    }

    private static (IReadOnlyList<string> Names, DataKind[] Kinds) InferSchema(Stream stream)
    {
        Dictionary<string, DataKind> columnKinds = new(StringComparer.OrdinalIgnoreCase);
        // Preserve insertion order for stable column ordering.
        List<string> orderedNames = new(32); // Expected small number of columns, so List is fine.
        int sampled = 0;

        using StreamReader reader = new(stream, leaveOpen: true);

        int lineNumber = 0;

        while (sampled < SchemaSampleSize)
        {
            string? line = reader.ReadLine();
            if (line is null) break;
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException ex)
            {
                throw new DeserializationException(
                    $"Malformed JSON at line {lineNumber}: {ex.Message}", lineNumber, ex);
            }

            using var _ = document;
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) continue;

            foreach (JsonProperty property in root.EnumerateObject())
            {
                DataKind detectedKind = JsonTypeInference.InferKind(property.Value);

                if (!columnKinds.TryGetValue(property.Name, out DataKind existingKind))
                {
                    columnKinds[property.Name] = detectedKind;
                    orderedNames.Add(property.Name);
                }
                else
                {
                    columnKinds[property.Name] = JsonTypeInference.WidenKind(existingKind, detectedKind);
                }
            }

            sampled++;
        }

        DataKind[] kinds = new DataKind[orderedNames.Count];
        for (int i = 0; i < orderedNames.Count; i++)
            kinds[i] = columnKinds[orderedNames[i]];

        return (orderedNames, kinds);
    }
}
