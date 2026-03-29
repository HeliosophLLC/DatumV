using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Execution;
using DatumIngest.Execution.Operators;
using DatumIngest.Functions;
using DatumIngest.Functions.Scalar.Strings;
using DatumIngest.Model;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;

namespace DatumIngest.Tests.Functions.Scalar.Strings;

/// <summary>
/// Tests for the PG-compliant string-length functions <see cref="LengthFunction"/>
/// (code-point count) and <see cref="OctetLengthFunction"/> (UTF-8 byte count),
/// plus their planner-time elision into <see cref="InlineAccessorExpression"/>.
/// </summary>
public sealed class StringLengthFunctionsTests : ServiceTestBase
{
    private static QueryOperator PlanQuery(string sql, TableCatalog catalog)
    {
        QueryExpression query = SqlParser.Parse(sql);
        QueryPlanner planner = new(catalog, FunctionRegistry.CreateDefault());
        return planner.Plan(query);
    }

    private TableCatalog CreateStringCatalog(params string[] values)
    {
        Pool pool = CreatePool();
        TableCatalog catalog = CreateCatalog(pool);
        object?[][] rows = new object?[values.Length][];
        for (int i = 0; i < values.Length; i++) rows[i] = [values[i]];
        catalog.Add(new InMemoryTableProvider(pool, "t",
            columns: ["s"],
            columnKinds: [DataKind.String],
            rows: rows));
        return catalog;
    }

    // ─────────────── Direct function semantics (ASCII) ───────────────

    [Fact]
    public async Task Length_AsciiString_ReturnsCharCount()
    {
        TableCatalog catalog = CreateStringCatalog("hello");
        List<Row> rows = await ExecuteQueryAsync("SELECT length(s) AS n FROM t", catalog);
        Assert.Equal(5, rows[0]["n"].AsInt32());
    }

    [Fact]
    public async Task OctetLength_AsciiString_ReturnsByteCount()
    {
        TableCatalog catalog = CreateStringCatalog("hello");
        List<Row> rows = await ExecuteQueryAsync("SELECT octet_length(s) AS n FROM t", catalog);
        Assert.Equal(5, rows[0]["n"].AsInt32());
    }

    [Fact]
    public async Task Length_NullString_ReturnsNull()
    {
        Pool pool = CreatePool();
        TableCatalog catalog = CreateCatalog(pool);
        catalog.Add(new InMemoryTableProvider(pool, "t",
            columns: ["s"],
            columnKinds: [DataKind.String],
            rows: [[null]]));

        List<Row> rows = await ExecuteQueryAsync("SELECT length(s) AS n, octet_length(s) AS b FROM t", catalog);
        Assert.True(rows[0]["n"].IsNull);
        Assert.True(rows[0]["b"].IsNull);
        Assert.Equal(DataKind.Int32, rows[0]["n"].Kind);
    }

    // ─────────────── Multi-byte semantics (the whole point of having both) ───────────────

    [Fact]
    public async Task Length_MultiByteLatin_CountsCodePointsNotBytes()
    {
        // "café" — 4 code points (c, a, f, é), 5 UTF-8 bytes (é is 2 bytes).
        // length() returns code points; octet_length() returns bytes.
        TableCatalog catalog = CreateStringCatalog("café");
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT length(s) AS chars, octet_length(s) AS bytes FROM t", catalog);
        Assert.Equal(4, rows[0]["chars"].AsInt32());
        Assert.Equal(5, rows[0]["bytes"].AsInt32());
    }

    [Fact]
    public async Task Length_SurrogatePair_CountsOneCodePoint()
    {
        // "😀" (U+1F600 grinning face) — 1 Unicode code point, 4 UTF-8 bytes,
        // 2 UTF-16 code units (surrogate pair). PG length() returns 1 here, in
        // contrast to .NET string.Length which would return 2. This is the
        // motivating case for the code-point rework.
        string emoji = char.ConvertFromUtf32(0x1F600);
        TableCatalog catalog = CreateStringCatalog(emoji);
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT length(s) AS chars, octet_length(s) AS bytes FROM t", catalog);
        Assert.Equal(1, rows[0]["chars"].AsInt32());
        Assert.Equal(4, rows[0]["bytes"].AsInt32());
    }

    [Fact]
    public async Task Length_MixedSurrogatesAndAscii_CountsCorrectly()
    {
        // "hi 😀!" — 5 code points (h, i, space, emoji, !), 8 bytes
        // (3 ASCII + 4 emoji + 1 ASCII).
        string s = "hi " + char.ConvertFromUtf32(0x1F600) + "!";
        TableCatalog catalog = CreateStringCatalog(s);
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT length(s) AS chars, octet_length(s) AS bytes FROM t", catalog);
        Assert.Equal(5, rows[0]["chars"].AsInt32());
        Assert.Equal(8, rows[0]["bytes"].AsInt32());
    }

    [Fact]
    public async Task Length_LongStringForcingArenaPath_StillCorrect()
    {
        // > 27 UTF-8 bytes forces the arena-backed path. The cached _charCount
        // is stamped at construction by CountCharSpanCodePoints, so the
        // function's RawCharCount fast-read returns the correct count even
        // though the value isn't inline.
        string s = "this string definitely doesn't fit inline " + char.ConvertFromUtf32(0x1F600);
        TableCatalog catalog = CreateStringCatalog(s);
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT length(s) AS chars, octet_length(s) AS bytes FROM t", catalog);

        // 42 ASCII chars + 1 emoji code point = 43 code points.
        // 42 ASCII bytes + 4 emoji bytes = 46 UTF-8 bytes.
        Assert.Equal(43, rows[0]["chars"].AsInt32());
        Assert.Equal(46, rows[0]["bytes"].AsInt32());
    }

    // ─────────────── Plan-shape elision ───────────────

    [Fact]
    public void LengthCall_RewritesToInlineAccessorExpression()
    {
        TableCatalog catalog = CreateStringCatalog("hello");
        QueryOperator plan = PlanQuery("SELECT length(s) FROM t", catalog);
        ProjectOperator project = Assert.IsType<ProjectOperator>(plan);
        InlineAccessorExpression elided = Assert.IsType<InlineAccessorExpression>(
            project.Columns[0].Expression);
        Assert.Equal(InlineAccessorField.StringCodePointLength, elided.Field);
    }

    [Fact]
    public void OctetLengthCall_RewritesToInlineAccessorExpression()
    {
        TableCatalog catalog = CreateStringCatalog("hello");
        QueryOperator plan = PlanQuery("SELECT octet_length(s) FROM t", catalog);
        ProjectOperator project = Assert.IsType<ProjectOperator>(plan);
        InlineAccessorExpression elided = Assert.IsType<InlineAccessorExpression>(
            project.Columns[0].Expression);
        Assert.Equal(InlineAccessorField.StringByteLength, elided.Field);
    }

    [Fact]
    public async Task ElidedAndFunctionPathsAgree_ForLongString()
    {
        // The elided node falls back to the function when the inline DataValue
        // isn't an inline-tier string. This exercises the fallback branch end-
        // to-end with an arena-backed value, ensuring the planner-level rewrite
        // doesn't change observable results for long strings.
        string s = new('x', 100); // way past MaxInlineUtf8Bytes
        TableCatalog catalog = CreateStringCatalog(s);
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT length(s) AS chars, octet_length(s) AS bytes FROM t", catalog);
        Assert.Equal(100, rows[0]["chars"].AsInt32());
        Assert.Equal(100, rows[0]["bytes"].AsInt32());
    }
}
