using System.Runtime.CompilerServices;
using System.Text.Json;
using DatumIngest.Model;

namespace DatumIngest.Catalog.Providers;

/// <summary>
/// Reads newline-delimited JSON (JSONL/NDJSON) files where each line is a
/// self-contained JSON object. Streams line-by-line for constant memory usage
/// regardless of file size. Type inference and value conversion are shared
/// with <see cref="JsonTableProvider"/> via <see cref="JsonTypeInference"/>.
/// </summary>
public sealed class JsonlTableProvider : ITableProvider
{
    /// <summary>Maximum number of lines sampled for schema inference.</summary>
    private const int SchemaSampleSize = 100;

    /// <inheritdoc />
    public async Task<Schema> GetSchemaAsync(
        TableDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        Dictionary<string, DataKind> columnKinds = new(StringComparer.OrdinalIgnoreCase);
        int sampled = 0;

        using StreamReader reader = new(descriptor.FilePath);

        while (sampled < SchemaSampleSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string? line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (JsonProperty property in root.EnumerateObject())
            {
                DataKind detectedKind = JsonTypeInference.InferKind(property.Value);

                if (!columnKinds.TryGetValue(property.Name, out DataKind existingKind))
                {
                    columnKinds[property.Name] = detectedKind;
                }
                else
                {
                    columnKinds[property.Name] = JsonTypeInference.WidenKind(existingKind, detectedKind);
                }
            }

            sampled++;
        }

        List<ColumnInfo> columns = new(columnKinds.Count);
        foreach (KeyValuePair<string, DataKind> entry in columnKinds)
        {
            columns.Add(new ColumnInfo(entry.Key, entry.Value, nullable: true));
        }

        return new Schema(columns);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<Row> OpenAsync(
        TableDescriptor descriptor,
        IReadOnlySet<string>? requiredColumns,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Schema schema = await GetSchemaAsync(descriptor, cancellationToken);

        List<ColumnInfo> projectedColumns;
        if (requiredColumns is not null)
        {
            projectedColumns = new List<ColumnInfo>();
            foreach (ColumnInfo column in schema.Columns)
            {
                if (requiredColumns.Contains(column.Name))
                {
                    projectedColumns.Add(column);
                }
            }
        }
        else
        {
            projectedColumns = new List<ColumnInfo>(schema.Columns);
        }

        string[] names = new string[projectedColumns.Count];
        for (int index = 0; index < projectedColumns.Count; index++)
        {
            names[index] = projectedColumns[index].Name;
        }

        Dictionary<string, int> nameIndex = new(names.Length, StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < names.Length; index++)
        {
            nameIndex[names[index]] = index;
        }

        using StreamReader reader = new(descriptor.FilePath);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string? line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            DataValue[] values = new DataValue[projectedColumns.Count];
            for (int columnIndex = 0; columnIndex < projectedColumns.Count; columnIndex++)
            {
                ColumnInfo column = projectedColumns[columnIndex];
                if (root.TryGetProperty(column.Name, out JsonElement propertyValue))
                {
                    values[columnIndex] = JsonTypeInference.ConvertElement(propertyValue, column.Kind);
                }
                else
                {
                    values[columnIndex] = DataValue.Null(column.Kind);
                }
            }

            yield return new Row(names, values, nameIndex);
        }
    }

    /// <inheritdoc />
    public Task<ProviderCapabilities> GetCapabilitiesAsync(
        TableDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(new ProviderCapabilities(
            EstimatedRowCount: null,
            EstimatedRowSizeBytes: null,
            SupportsSeek: false,
            ColumnCosts: new Dictionary<string, ColumnCost>()));
    }
}
