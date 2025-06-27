using System.Runtime.CompilerServices;
using System.Text.Json;
using Axon.QueryEngine.Model;

namespace Axon.QueryEngine.Catalog.Providers;

/// <summary>
/// Reads JSON files containing arrays of objects. Supports root-level arrays
/// and nested arrays via a dot-delimited <c>json_path</c> option.
/// Complex values (arrays, objects) are preserved as <see cref="DataKind.JsonValue"/>.
/// </summary>
public sealed class JsonTableProvider : ITableProvider
{
    /// <inheritdoc />
    public async Task<Schema> GetSchemaAsync(
        TableDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        JsonDocument document = await LoadDocumentAsync(descriptor, cancellationToken);
        using (document)
        {
            JsonElement array = NavigateToArray(document.RootElement, descriptor);

            // Union of all property names from first pass (up to 100 elements)
            Dictionary<string, DataKind> columnKinds = new(StringComparer.OrdinalIgnoreCase);
            int sampled = 0;

            foreach (JsonElement element in array.EnumerateArray())
            {
                if (sampled >= 100)
                {
                    break;
                }

                if (element.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (JsonProperty property in element.EnumerateObject())
                {
                    DataKind detectedKind = InferKind(property.Value);

                    if (!columnKinds.TryGetValue(property.Name, out DataKind existingKind))
                    {
                        columnKinds[property.Name] = detectedKind;
                    }
                    else if (existingKind != detectedKind)
                    {
                        // If types conflict, widen to String (unless one is already JsonValue)
                        if (existingKind == DataKind.JsonValue || detectedKind == DataKind.JsonValue)
                        {
                            columnKinds[property.Name] = DataKind.JsonValue;
                        }
                        else
                        {
                            columnKinds[property.Name] = DataKind.String;
                        }
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
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<Row> OpenAsync(
        TableDescriptor descriptor,
        IReadOnlySet<string>? requiredColumns,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Schema schema = await GetSchemaAsync(descriptor, cancellationToken);

        JsonDocument document = await LoadDocumentAsync(descriptor, cancellationToken);
        using (document)
        {
            JsonElement array = NavigateToArray(document.RootElement, descriptor);

            // Build column index for projection
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

            foreach (JsonElement element in array.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (element.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                DataValue[] values = new DataValue[projectedColumns.Count];
                for (int columnIndex = 0; columnIndex < projectedColumns.Count; columnIndex++)
                {
                    ColumnInfo column = projectedColumns[columnIndex];
                    if (element.TryGetProperty(column.Name, out JsonElement propertyValue))
                    {
                        values[columnIndex] = ConvertElement(propertyValue, column.Kind);
                    }
                    else
                    {
                        values[columnIndex] = DataValue.Null(column.Kind);
                    }
                }

                yield return new Row(names, values);
            }
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

    /// <summary>
    /// Loads the entire JSON file into a <see cref="JsonDocument"/>.
    /// </summary>
    private static async Task<JsonDocument> LoadDocumentAsync(
        TableDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        using FileStream stream = File.OpenRead(descriptor.FilePath);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Navigates from the root element to the target array using the
    /// dot-delimited <c>json_path</c> option. If no path is specified,
    /// the root element must itself be an array.
    /// </summary>
    private static JsonElement NavigateToArray(JsonElement root, TableDescriptor descriptor)
    {
        if (!descriptor.Options.TryGetValue("json_path", out string? jsonPath) ||
            string.IsNullOrEmpty(jsonPath))
        {
            if (root.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException(
                    "JSON root is not an array. Specify a 'json_path' option to navigate to an array property.");
            }
            return root;
        }

        JsonElement current = root;
        string[] segments = jsonPath.Split('.');

        foreach (string segment in segments)
        {
            if (current.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    $"Cannot navigate to '{segment}': current element is not an object.");
            }

            if (!current.TryGetProperty(segment, out JsonElement next))
            {
                throw new InvalidOperationException(
                    $"Property '{segment}' not found in JSON path '{jsonPath}'.");
            }

            current = next;
        }

        if (current.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                $"JSON path '{jsonPath}' does not resolve to an array.");
        }

        return current;
    }

    /// <summary>
    /// Infers the <see cref="DataKind"/> for a JSON element value.
    /// </summary>
    private static DataKind InferKind(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Number => DataKind.Scalar,
            JsonValueKind.String => DataKind.String,
            JsonValueKind.True or JsonValueKind.False => DataKind.Scalar,
            JsonValueKind.Null or JsonValueKind.Undefined => DataKind.String,
            JsonValueKind.Array or JsonValueKind.Object => DataKind.JsonValue,
            _ => DataKind.String
        };
    }

    /// <summary>
    /// Converts a JSON element to a <see cref="DataValue"/> based on the target column kind.
    /// </summary>
    private static DataValue ConvertElement(JsonElement element, DataKind targetKind)
    {
        if (element.ValueKind == JsonValueKind.Null || element.ValueKind == JsonValueKind.Undefined)
        {
            return DataValue.Null(targetKind);
        }

        return targetKind switch
        {
            DataKind.Scalar => element.ValueKind switch
            {
                JsonValueKind.Number => DataValue.FromScalar((float)element.GetDouble()),
                JsonValueKind.True => DataValue.FromScalar(1f),
                JsonValueKind.False => DataValue.FromScalar(0f),
                _ => DataValue.Null(DataKind.Scalar)
            },
            DataKind.String => DataValue.FromString(element.GetString() ?? element.GetRawText()),
            DataKind.JsonValue => DataValue.FromJsonValue(element.GetRawText()),
            _ => DataValue.FromString(element.GetRawText())
        };
    }
}
