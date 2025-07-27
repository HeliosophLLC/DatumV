using System.Runtime.CompilerServices;
using System.Text.Json;
using DatumIngest.Model;

namespace DatumIngest.Catalog.Providers;

/// <summary>
/// Reads JSON files containing arrays of objects. Supports root-level arrays
/// and nested arrays via a dot-delimited <c>json_path</c> option.
/// Complex values (arrays, objects) are preserved as <see cref="DataKind.JsonValue"/>.
/// When the root element is an object, implements <see cref="IMultiTableSource"/> to
/// auto-discover top-level array properties as separate sub-tables.
/// </summary>
public sealed class JsonTableProvider : IMultiTableSource
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

            // Check if this is a primitive array (first element is not an object).
            if (IsPrimitiveArray(array))
            {
                DataKind kind = InferPrimitiveArrayKind(array);
                return new Schema(new[] { new ColumnInfo("value", kind, nullable: true) });
            }

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

            Dictionary<string, int> nameIndex = new(names.Length, StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < names.Length; index++)
            {
                nameIndex[names[index]] = index;
            }

            // Primitive array: yield single-column rows.
            if (IsPrimitiveArray(array))
            {
                DataKind kind = schema.Columns[0].Kind;
                string[] primitiveNames = new[] { "value" };
                Dictionary<string, int> primitiveNameIndex = new(1, StringComparer.OrdinalIgnoreCase) { ["value"] = 0 };

                foreach (JsonElement element in array.EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    DataValue[] primitiveValues = new[] { JsonTypeInference.ConvertElement(element, kind) };
                    yield return new Row(primitiveNames, primitiveValues, primitiveNameIndex);
                }

                yield break;
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
                string message = "JSON root is not an array.";

                if (root.ValueKind == JsonValueKind.Object)
                {
                    List<string> arrayProperties = new();
                    foreach (JsonProperty property in root.EnumerateObject())
                    {
                        if (property.Value.ValueKind == JsonValueKind.Array)
                        {
                            arrayProperties.Add(property.Name);
                        }
                    }

                    if (arrayProperties.Count > 0)
                    {
                        message += $" The root object contains array properties: [{string.Join(", ", arrayProperties)}]." +
                                   " Specify a 'json_path' option to select one, or use catalog expansion (ExpandMultiTableSourcesAsync)" +
                                   " to register each as a separate table.";
                    }
                    else
                    {
                        message += " The root object contains no array properties.";
                    }
                }
                else
                {
                    message += " Specify a 'json_path' option to navigate to an array property.";
                }

                throw new InvalidOperationException(message);
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
    /// Checks whether the array contains primitive values (not objects).
    /// An empty array is not considered primitive.
    /// </summary>
    private static bool IsPrimitiveArray(JsonElement array)
    {
        foreach (JsonElement element in array.EnumerateArray())
        {
            return element.ValueKind != JsonValueKind.Object;
        }

        return false;
    }

    /// <summary>
    /// Infers the <see cref="DataKind"/> for a primitive array by sampling up to 100 elements.
    /// </summary>
    private static DataKind InferPrimitiveArrayKind(JsonElement array)
    {
        DataKind kind = DataKind.String;
        int sampled = 0;

        foreach (JsonElement element in array.EnumerateArray())
        {
            if (sampled >= 100)
            {
                break;
            }

            DataKind detectedKind = JsonTypeInference.InferKind(element);

            if (sampled == 0)
            {
                kind = detectedKind;
            }
            else
            {
                kind = JsonTypeInference.WidenKind(kind, detectedKind);
            }

            sampled++;
        }

        return kind;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DiscoveredTable>?> DiscoverTablesAsync(
        TableDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        // Explicit json_path overrides discovery — treat as single table.
        if (descriptor.Options.TryGetValue("json_path", out string? existingPath) &&
            !string.IsNullOrEmpty(existingPath))
        {
            return null;
        }

        JsonDocument document = await LoadDocumentAsync(descriptor, cancellationToken);
        using (document)
        {
            // Root must be an object to discover sub-tables.
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            List<DiscoveredTable> tables = new();

            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                // Use the leaf property name as sub-table key.
                Dictionary<string, string> options = new(descriptor.Options, StringComparer.OrdinalIgnoreCase)
                {
                    ["json_path"] = property.Name
                };

                tables.Add(new DiscoveredTable(property.Name, options));
            }

            return tables.Count > 0 ? tables : null;
        }
    }
}
