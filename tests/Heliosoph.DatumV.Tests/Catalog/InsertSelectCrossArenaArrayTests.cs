using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Catalog;

/// <summary>
/// Cross-arena <c>INSERT … SELECT</c> of typed-array columns. Until this
/// landed, the executor's same-arena gate threw on every multi-table
/// array copy; these tests pin the supported and rejected element kinds.
/// </summary>
public sealed class InsertSelectCrossArenaArrayTests : ServiceTestBase, IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"datum_xarena_{Guid.NewGuid():N}");
    private string CatalogPath => Path.Combine(_tempDir, ".datum-catalog.json");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
        }
        return Task.CompletedTask;
    }

    private TableCatalog NewFileCatalog() => CreateCatalog(CatalogPath);

    // ───────────────────── Flat 1-D primitive arrays ─────────────────────

    [Fact]
    public async Task FlatFloat32Array_CrossArenaCopy_PreservesElements()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE source (v Array<Float32>(4))");
        catalog.Plan("CREATE TABLE sink (v Array<Float32>(4))");
        catalog.Plan("INSERT INTO source VALUES ([1.0, 2.0, 3.0, 4.0]), ([10.0, 20.0, 30.0, 40.0])");

        // Cross-arena copy via INSERT … SELECT — this used to throw.
        catalog.Plan("INSERT INTO sink SELECT v FROM source");

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT cardinality(v) AS n, v[1] AS first, v[4] AS last FROM sink", catalog);

        Assert.Equal(2, rows.Count);
        Assert.Equal(4, rows[0]["n"].AsInt32());
        Assert.Equal(1f, rows[0]["first"].AsFloat32());
        Assert.Equal(4f, rows[0]["last"].AsFloat32());
        Assert.Equal(10f, rows[1]["first"].AsFloat32());
        Assert.Equal(40f, rows[1]["last"].AsFloat32());
    }

    [Fact]
    public async Task FlatInt32Array_VariableLength_CrossArenaCopy_Works()
    {
        // Variable-length array column (no FixedShape) exercises the
        // EnforceFixedShape no-op path post-copy.
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE source (v Array<Int32>)");
        catalog.Plan("CREATE TABLE sink (v Array<Int32>)");
        catalog.Plan(
            "INSERT INTO source VALUES " +
            "([CAST(1 AS Int32), CAST(2 AS Int32), CAST(3 AS Int32)]), " +
            "([CAST(100 AS Int32), CAST(200 AS Int32)])");

        catalog.Plan("INSERT INTO sink SELECT v FROM source");

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT cardinality(v) AS n FROM sink", catalog);

        Assert.Equal(2, rows.Count);
        int[] counts = rows.Select(r => r["n"].AsInt32()).OrderBy(n => n).ToArray();
        Assert.Equal([2, 3], counts);
    }

    // ───────────────────── Multi-dim arrays ─────────────────────

    [Fact]
    public async Task MultiDimArray_CrossArenaCopy_PreservesShapeAndElements()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE source (m Array<Float32>(2, 3))");
        catalog.Plan("CREATE TABLE sink (m Array<Float32>(2, 3))");
        catalog.Plan("INSERT INTO source VALUES ([1.0, 2.0, 3.0, 4.0, 5.0, 6.0])");

        // Cross-arena copy — shape prefix must survive intact.
        catalog.Plan("INSERT INTO sink SELECT m FROM source");

        using Arena arena = CreateArena();
        arena.AddReference();
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT array_shape(m) AS shape," +
            "       array_ndims(m) AS nd," +
            "       m[1, 1]        AS top_left," +
            "       m[2, 3]        AS bottom_right" +
            " FROM sink", catalog, store: arena);

        Assert.Single(rows);
        Assert.Equal(2, rows[0]["nd"].AsInt32());
        Assert.Equal([2, 3],
            rows[0]["shape"].AsArraySpan<int>(arena, catalog.SidecarRegistry).ToArray());
        Assert.Equal(1f, rows[0]["top_left"].AsFloat32());
        Assert.Equal(6f, rows[0]["bottom_right"].AsFloat32());
    }

    // ───────────────────── Reference-element arrays: String + Image ─────────────────────

    [Fact]
    public async Task StringArray_CrossArenaCopy_PreservesElements()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE source (s Array<String>)");
        catalog.Plan("CREATE TABLE sink (s Array<String>)");
        catalog.Plan("INSERT INTO source VALUES (['alpha', 'beta', 'gamma'])");

        // Cross-arena copy via existing AsStringArray + FromStringArray pair —
        // managed string[] intermediate is arena-independent, so the new slot
        // block + UTF-8 bodies land in the target arena with no source-arena
        // references surviving.
        catalog.Plan("INSERT INTO sink SELECT s FROM source");

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT cardinality(s) AS n, s[1] AS first, s[3] AS last FROM sink", catalog);

        Assert.Single(rows);
        Assert.Equal(3, rows[0]["n"].AsInt32());
        Assert.Equal("alpha", rows[0]["first"].AsString());
        Assert.Equal("gamma", rows[0]["last"].AsString());
    }

    // Array<Audio>, Array<Video>, Array<Json>, Array<PointCloud> cross-arena
    // copy is unit-tested at the DataValue.From*Array / As*Array round-trip
    // level in tests/Heliosoph.DatumV.Tests/Model/ReferenceArrayBlobKindsTests.cs —
    // those kinds don't have SQL literal syntax, so we can't drive the
    // catalog-level cross-arena path from a SQL test. Unit tests cover the
    // factory pair correctness; the InsertExecutor dispatch arm just routes
    // through that same pair.

    // Array<Struct> cross-arena copy is rejected in code (per-field rebinding
    // not implemented), but the rejection isn't directly testable through SQL
    // today — struct-typed column DDL with inline field declarations doesn't
    // parse. The rejection branch in InsertExecutor.CopyTypedArrayToTargetArena
    // is reachable only via programmatic catalog setup, which is out of scope
    // for these tests. When struct DDL syntax lands (or per-field rebind
    // closes the gap), add the corresponding test here.

    // ───────────────────── Same-arena pass-through (regression) ─────────────────────

    [Fact]
    public void SameArena_FastPath_StillWorks()
    {
        // VALUES path: source DataValue is built in the target arena, so the
        // cross-arena copy isn't invoked. Regression coverage for the
        // ReferenceEquals fast-path branch.
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (v Array<Float32>(4))");
        catalog.Plan("INSERT INTO t VALUES ([1.0, 2.0, 3.0, 4.0])");
        Assert.Equal(1, catalog["t"].GetRowCount());
    }

    // ───────────────────── Byte arrays ─────────────────────

    [Fact]
    public async Task ByteArray_CrossArenaCopy_PreservesBytes()
    {
        // UInt8 + IsArray uses a raw byte payload with no slot blocks —
        // the fixed-width byte-copy path handles it the same as a primitive
        // array. Multi-dim byte arrays are rejected at DDL, so this is the
        // only byte-array variant the cross-arena path needs to handle.
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE source (b Array<UInt8>(4))");
        catalog.Plan("CREATE TABLE sink (b Array<UInt8>(4))");
        catalog.Plan("INSERT INTO source VALUES ([CAST(10 AS UInt8), CAST(20 AS UInt8), CAST(30 AS UInt8), CAST(40 AS UInt8)])");

        catalog.Plan("INSERT INTO sink SELECT b FROM source");

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT cardinality(b) AS n, b[1] AS first, b[4] AS last FROM sink", catalog);
        Assert.Single(rows);
        Assert.Equal(4, rows[0]["n"].AsInt32());
        Assert.Equal((byte)10, rows[0]["first"].AsUInt8());
        Assert.Equal((byte)40, rows[0]["last"].AsUInt8());
    }
}
