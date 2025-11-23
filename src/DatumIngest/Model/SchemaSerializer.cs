namespace DatumIngest.Model;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Serializes and deserializes <see cref="SourceSchema"/> using System.Text.Json
/// with source generation for AOT/trimming compatibility.
/// </summary>
public static class SchemaSerializer
{
    /// <summary>
    /// Serializes a source schema to a JSON string.
    /// </summary>
    public static string Serialize(SourceSchema sourceSchema)
    {
        SchemaDocument document = SchemaDocument.FromSourceSchema(sourceSchema);
        return JsonSerializer.Serialize(document, SchemaJsonContext.Default.SchemaDocument);
    }

    /// <summary>
    /// Serializes a single-table schema to a JSON string by wrapping it in a
    /// <see cref="SourceSchema"/> keyed by <paramref name="tableName"/>.
    /// </summary>
    /// <param name="tableName">Catalog table name used as the dictionary key.</param>
    /// <param name="schema">The single-table schema to serialize.</param>
    public static string Serialize(string tableName, Schema schema)
    {
        return Serialize(SourceSchema.Create(tableName, schema));
    }

    /// <summary>
    /// Serializes a source schema to a UTF-8 byte array.
    /// </summary>
    public static byte[] SerializeToUtf8Bytes(SourceSchema sourceSchema)
    {
        SchemaDocument document = SchemaDocument.FromSourceSchema(sourceSchema);
        return JsonSerializer.SerializeToUtf8Bytes(document, SchemaJsonContext.Default.SchemaDocument);
    }

    /// <summary>
    /// Deserializes a source schema from a JSON string.
    /// </summary>
    public static SourceSchema? Deserialize(string json)
    {
        SchemaDocument? document = JsonSerializer.Deserialize(json, SchemaJsonContext.Default.SchemaDocument);
        return document?.ToSourceSchema();
    }

    /// <summary>
    /// Writes a source schema to a file as formatted JSON.
    /// </summary>
    public static async Task WriteToFileAsync(SourceSchema sourceSchema, string path)
    {
        string json = Serialize(sourceSchema);
        await File.WriteAllTextAsync(path, json);
    }

    /// <summary>
    /// Writes a single-table schema to a file as formatted JSON by wrapping it in a
    /// <see cref="SourceSchema"/> keyed by <paramref name="tableName"/>.
    /// </summary>
    /// <param name="tableName">Catalog table name used as the dictionary key.</param>
    /// <param name="schema">The single-table schema to write.</param>
    /// <param name="path">Output file path.</param>
    public static async Task WriteToFileAsync(string tableName, Schema schema, string path)
    {
        await WriteToFileAsync(SourceSchema.Create(tableName, schema), path);
    }
}

/// <summary>
/// JSON-serializable document representing a <see cref="SourceSchema"/>.
/// Maps the <see cref="Schema"/> domain objects to/from a flat structure
/// compatible with System.Text.Json source generation.
/// </summary>
internal sealed class SchemaDocument
{
    /// <summary>
    /// Per-table column definitions, keyed by catalog table name.
    /// </summary>
    public Dictionary<string, List<SchemaColumnEntry>> Tables { get; set; } = new();

    /// <summary>
    /// Converts a <see cref="SourceSchema"/> to a serializable document.
    /// </summary>
    internal static SchemaDocument FromSourceSchema(SourceSchema sourceSchema)
    {
        SchemaDocument document = new();

        foreach (KeyValuePair<string, Schema> entry in sourceSchema.Tables)
        {
            List<SchemaColumnEntry> columns = new(entry.Value.Columns.Count);

            foreach (ColumnInfo column in entry.Value.Columns)
            {
                columns.Add(new SchemaColumnEntry
                {
                    Name = column.Name,
                    Kind = column.Kind,
                    Nullable = column.Nullable,
                    IsArray = column.IsArray,
                });
            }

            document.Tables[entry.Key] = columns;
        }

        return document;
    }

    /// <summary>
    /// Converts this document back to a <see cref="SourceSchema"/>.
    /// </summary>
    internal SourceSchema ToSourceSchema()
    {
        Dictionary<string, Schema> tables = new(StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, List<SchemaColumnEntry>> entry in Tables)
        {
            List<ColumnInfo> columns = new(entry.Value.Count);

            foreach (SchemaColumnEntry columnEntry in entry.Value)
            {
                columns.Add(new ColumnInfo(columnEntry.Name, columnEntry.Kind, columnEntry.Nullable)
                {
                    IsArray = columnEntry.IsArray,
                });
            }

            tables[entry.Key] = new Schema(columns);
        }

        return new SourceSchema { Tables = tables };
    }
}

/// <summary>
/// JSON-serializable representation of a single column within a schema.
/// </summary>
internal sealed class SchemaColumnEntry
{
    /// <summary>Column name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Data kind.</summary>
    public DataKind Kind { get; set; }

    /// <summary>Whether the column may contain null values.</summary>
    public bool Nullable { get; set; }

    /// <summary>
    /// True when this column holds typed arrays of <see cref="Kind"/> elements
    /// (byte arrays, integer arrays, etc.). Defaults to <c>false</c>; absent in
    /// JSON when the column is scalar. Mirrors <see cref="ColumnInfo.IsArray"/>.
    /// </summary>
    public bool IsArray { get; set; }
}

/// <summary>
/// Source-generated JSON serializer context for the schema sidecar type hierarchy.
/// Enables AOT compilation and trimming support.
/// </summary>
[JsonSerializable(typeof(SchemaDocument))]
[JsonSerializable(typeof(SchemaColumnEntry))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class SchemaJsonContext : JsonSerializerContext
{
}
