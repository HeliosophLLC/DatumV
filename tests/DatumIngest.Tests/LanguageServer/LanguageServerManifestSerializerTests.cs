namespace DatumIngest.Tests.LanguageServer;

using DatumIngest.Manifest;

/// <summary>
/// Tests for <see cref="LanguageServerManifestSerializer"/> — round-trip JSON serialization
/// of the language server manifest using source-generated System.Text.Json.
/// </summary>
public sealed class LanguageServerManifestSerializerTests : ServiceTestBase
{
    private static LanguageServerManifest CreateManifest()
    {
        return new LanguageServerManifest
        {
            Tables =
            [
                new TableSchemaEntry
                {
                    Name = "sensors",
                    Columns =
                    [
                        new TableColumnEntry { Name = "timestamp", Kind = "DateTime", Nullable = false },
                        new TableColumnEntry { Name = "reading", Kind = "Float32", Nullable = true },
                    ],
                },
            ],
            Functions =
            [
                new FunctionSignature
                {
                    Name = "abs",
                    Parameters = [new ParameterSignature { Name = "value", Kind = "Float32" }],
                    ReturnType = "Float32",
                    Description = "Absolute value.",
                },
                new FunctionSignature
                {
                    Name = "unnest",
                    Parameters = [new ParameterSignature { Name = "array_column", Kind = "Vector" }],
                    ReturnType = "Float32",
                    Description = "Expands a vector column.",
                    IsTableValued = true,
                },
            ],
            Keywords = ["SELECT", "FROM", "WHERE"],
        };
    }

    // ───────────────────── Round-trip ─────────────────────

    [Fact]
    public void Serialize_ThenDeserialize_PreservesStructure()
    {
        LanguageServerManifest original = CreateManifest();

        string json = LanguageServerManifestSerializer.Serialize(original);
        LanguageServerManifest? restored = LanguageServerManifestSerializer.Deserialize(json);

        Assert.NotNull(restored);
        Assert.Equal(original.Version, restored.Version);
        Assert.Equal(original.Tables.Count, restored.Tables.Count);
        Assert.Equal(original.Functions.Count, restored.Functions.Count);
        Assert.Equal(original.Keywords.Count, restored.Keywords.Count);
    }

    [Fact]
    public void Serialize_PreservesTableSchema()
    {
        LanguageServerManifest original = CreateManifest();

        string json = LanguageServerManifestSerializer.Serialize(original);
        LanguageServerManifest? restored = LanguageServerManifestSerializer.Deserialize(json);

        Assert.NotNull(restored);
        TableSchemaEntry table = restored.Tables[0];
        Assert.Equal("sensors", table.Name);
        Assert.Equal(2, table.Columns.Count);
        Assert.Equal("timestamp", table.Columns[0].Name);
        Assert.Equal("DateTime", table.Columns[0].Kind);
        Assert.False(table.Columns[0].Nullable);
        Assert.True(table.Columns[1].Nullable);
    }

    [Fact]
    public void Serialize_PreservesFunctionSignatures()
    {
        LanguageServerManifest original = CreateManifest();

        string json = LanguageServerManifestSerializer.Serialize(original);
        LanguageServerManifest? restored = LanguageServerManifestSerializer.Deserialize(json);

        Assert.NotNull(restored);

        FunctionSignature scalarFunction = restored.Functions[0];
        Assert.Equal("abs", scalarFunction.Name);
        Assert.Equal("Float32", scalarFunction.ReturnType);
        Assert.False(scalarFunction.IsTableValued);
        Assert.Single(scalarFunction.Parameters);
        Assert.Equal("value", scalarFunction.Parameters[0].Name);

        FunctionSignature tableFunction = restored.Functions[1];
        Assert.Equal("unnest", tableFunction.Name);
        Assert.True(tableFunction.IsTableValued);
    }

    // ───────────────────── JSON format ─────────────────────

    [Fact]
    public void Serialize_UsesCamelCaseNaming()
    {
        string json = LanguageServerManifestSerializer.Serialize(CreateManifest());

        Assert.Contains("\"version\":", json);
        Assert.Contains("\"tables\":", json);
        Assert.Contains("\"functions\":", json);
        Assert.Contains("\"keywords\":", json);
        Assert.Contains("\"returnType\":", json);
        Assert.Contains("\"isTableValued\":", json);
    }

    [Fact]
    public void Serialize_OmitsNullValues()
    {
        LanguageServerManifest manifest = new()
        {
            Tables = [],
            Functions =
            [
                new FunctionSignature
                {
                    Name = "coalesce",
                    Parameters = [new ParameterSignature { Name = "values", Kind = "Any" }],
                    // ReturnType and Description are null.
                },
            ],
            Keywords = [],
        };

        string json = LanguageServerManifestSerializer.Serialize(manifest);

        // Null properties should be omitted entirely (WhenWritingNull).
        Assert.DoesNotContain("\"returnType\":", json);
        Assert.DoesNotContain("\"description\":", json);
    }
}
