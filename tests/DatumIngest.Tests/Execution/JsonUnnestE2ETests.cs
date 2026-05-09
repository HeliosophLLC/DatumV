using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Functions.Json;
using DatumIngest.Model;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// End-to-end SQL coverage for <c>unnest(json_column)</c>: builds a
/// JSON-typed column whose value is a CBOR-encoded array, runs a SQL
/// query that unnests it, and asserts that the planner + executor row-
/// multiply correctly. Mirrors the typed-array end-to-end tests in
/// <see cref="ProjectionSetReturningTests"/>.
/// </summary>
public sealed class JsonUnnestE2ETests : ServiceTestBase
{
    private static byte[] CborOf(string json) => CborJsonCodec.EncodeFromJsonText(json);

    private TableCatalog CatalogWithJsonRow(string name, string json)
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Add(new InMemoryTableProvider(
            CreatePool(),
            name,
            columns: ["id", "annotations"],
            columnKinds: [DataKind.UInt8, DataKind.Json],
            rows: [[(byte)1, CborOf(json)]]));
        return catalog;
    }

    [Fact]
    public async Task UnnestJson_InProjection_ProducesOneRowPerElement()
    {
        TableCatalog catalog = CatalogWithJsonRow("coco",
            """[{"id":1,"caption":"a"},{"id":2,"caption":"b"},{"id":3,"caption":"c"}]""");

        // Projection-list unnest gets lifted by ProjectionSetReturningRewriter
        // into a synthesized FROM source, then the operator emits one row per
        // JSON array element.
        List<Row> results = await ExecuteQueryAsync(
            "SELECT unnest(annotations) FROM coco",
            catalog);

        Assert.Equal(3, results.Count);
        for (int i = 0; i < results.Count; i++)
        {
            Assert.Equal(DataKind.Json, results[i]["value"].Kind);
            Assert.False(results[i]["value"].IsNull);
        }
    }

    [Fact]
    public async Task UnnestJson_PrimitiveElements_YieldJsonScalars()
    {
        TableCatalog catalog = CatalogWithJsonRow("coco", """[10, 20, 30]""");

        List<Row> results = await ExecuteQueryAsync(
            "SELECT unnest(annotations) FROM coco",
            catalog);

        Assert.Equal(3, results.Count);
        Assert.Equal(DataKind.Json, results[0]["value"].Kind);
    }

    [Fact]
    public async Task UnnestJson_OnNullJson_YieldsNoRows()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Add(new InMemoryTableProvider(
            CreatePool(),
            "t",
            columns: ["id", "j"],
            columnKinds: [DataKind.UInt8, DataKind.Json],
            rows: [[(byte)1, null]]));

        List<Row> results = await ExecuteQueryAsync(
            "SELECT unnest(j) FROM t",
            catalog);

        Assert.Empty(results);
    }

    [Fact]
    public async Task UnnestJson_OnEmptyArray_YieldsNoRows()
    {
        TableCatalog catalog = CatalogWithJsonRow("t", "[]");

        List<Row> results = await ExecuteQueryAsync(
            "SELECT unnest(annotations) FROM t",
            catalog);

        Assert.Empty(results);
    }

    [Fact]
    public async Task UnnestJson_LateralPerRow_FansOutAcrossSourceRows()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Add(new InMemoryTableProvider(
            CreatePool(),
            "docs",
            columns: ["doc_id", "tags"],
            columnKinds: [DataKind.UInt8, DataKind.Json],
            rows:
            [
                [(byte)1, CborOf("""["red","green"]""")],
                [(byte)2, CborOf("""["blue"]""")],
            ]));

        // SELECT col, unnest(col) FROM t is lifted by ProjectionSetReturningRewriter
        // into a CROSS JOIN LATERAL — one inner element per outer row.
        List<Row> results = await ExecuteQueryAsync(
            "SELECT doc_id, unnest(tags) FROM docs",
            catalog);

        Assert.Equal(3, results.Count);
        Assert.Equal(1, results[0]["doc_id"].AsUInt8());
        Assert.Equal(1, results[1]["doc_id"].AsUInt8());
        Assert.Equal(2, results[2]["doc_id"].AsUInt8());
    }
}
