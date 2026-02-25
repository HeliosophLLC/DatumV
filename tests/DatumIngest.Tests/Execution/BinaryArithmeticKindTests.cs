using DatumIngest.Catalog;
using DatumIngest.Model;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Pins the runtime kind-promotion rules for binary and unary arithmetic.
/// Before the kind-promoted dispatch, every arithmetic op produced
/// <see cref="DataKind.Float32"/>, which silently lost precision on Int64
/// sums and threw outright on Int128 / Decimal. These tests assert each
/// branch of the promotion table:
/// <list type="bullet">
/// <item>integer + integer stays integer (widened per C# rules)</item>
/// <item>any float operand pulls the result into the wider float kind</item>
/// <item>Decimal precedence over float and integer</item>
/// <item>Int128 preservation</item>
/// <item>Divide and Power return float regardless of operand kinds</item>
/// </list>
/// </summary>
public sealed class BinaryArithmeticKindTests : ServiceTestBase
{
    /// <summary>
    /// Builds a single-row catalog whose column kinds are explicit DataValues —
    /// this lets each test pin operand kinds without relying on the parser's
    /// numeric-literal narrowing rules.
    /// </summary>
    private TableCatalog SingleRow(params (string Name, DataValue Value)[] columns)
    {
        string[] names = new string[columns.Length];
        object?[] cells = new object?[columns.Length];
        for (int i = 0; i < columns.Length; i++)
        {
            names[i] = columns[i].Name;
            cells[i] = columns[i].Value;
        }
        return CreateCatalog("data", names, cells);
    }

    // ───────────────────── Integer arithmetic preserves integer kind ─────────────────────

    [Fact]
    public async Task Int32_Plus_Int32_StaysInt32()
    {
        TableCatalog catalog = SingleRow(
            ("a", DataValue.FromInt32(5)),
            ("b", DataValue.FromInt32(7)));
        List<Row> rows = await ExecuteQueryAsync("SELECT a + b AS r FROM data", catalog);

        Assert.Equal(DataKind.Int32, rows[0]["r"].Kind);
        Assert.Equal(12, rows[0]["r"].AsInt32());
    }

    [Fact]
    public async Task Int64_Plus_Int64_StaysInt64()
    {
        // The headline case from the user's report — summing INT64 file
        // sizes used to silently coerce through Float32, losing precision
        // past ~10⁷ bytes.
        TableCatalog catalog = SingleRow(
            ("a", DataValue.FromInt64(10_000_000_001L)),
            ("b", DataValue.FromInt64(20_000_000_002L)));
        List<Row> rows = await ExecuteQueryAsync("SELECT a + b AS r FROM data", catalog);

        Assert.Equal(DataKind.Int64, rows[0]["r"].Kind);
        Assert.Equal(30_000_000_003L, rows[0]["r"].AsInt64());
    }

    [Fact]
    public async Task SmallInt_Plus_SmallInt_WidensToInt32()
    {
        // Mirrors C#'s `byte + byte → int` promotion: small operands
        // widen to Int32 so the result has headroom for accumulation.
        TableCatalog catalog = SingleRow(
            ("a", DataValue.FromInt8(100)),
            ("b", DataValue.FromInt8(50)));
        List<Row> rows = await ExecuteQueryAsync("SELECT a + b AS r FROM data", catalog);

        Assert.Equal(DataKind.Int32, rows[0]["r"].Kind);
        Assert.Equal(150, rows[0]["r"].AsInt32());
    }

    [Fact]
    public async Task Int32_Times_Int32_StaysInt32()
    {
        TableCatalog catalog = SingleRow(
            ("a", DataValue.FromInt32(6)),
            ("b", DataValue.FromInt32(7)));
        List<Row> rows = await ExecuteQueryAsync("SELECT a * b AS r FROM data", catalog);

        Assert.Equal(DataKind.Int32, rows[0]["r"].Kind);
        Assert.Equal(42, rows[0]["r"].AsInt32());
    }

    [Fact]
    public async Task Int32_Modulo_Int32_StaysInt32()
    {
        TableCatalog catalog = SingleRow(
            ("a", DataValue.FromInt32(17)),
            ("b", DataValue.FromInt32(5)));
        List<Row> rows = await ExecuteQueryAsync("SELECT a % b AS r FROM data", catalog);

        Assert.Equal(DataKind.Int32, rows[0]["r"].Kind);
        Assert.Equal(2, rows[0]["r"].AsInt32());
    }

    // ───────────────────── Mixed integer / float promotion ─────────────────────

    [Fact]
    public async Task Int32_Plus_Float32_PromotesToFloat32()
    {
        TableCatalog catalog = SingleRow(
            ("a", DataValue.FromInt32(5)),
            ("b", DataValue.FromFloat32(0.5f)));
        List<Row> rows = await ExecuteQueryAsync("SELECT a + b AS r FROM data", catalog);

        Assert.Equal(DataKind.Float32, rows[0]["r"].Kind);
        Assert.Equal(5.5f, rows[0]["r"].AsFloat32());
    }

    [Fact]
    public async Task Int64_Plus_Float64_PromotesToFloat64()
    {
        TableCatalog catalog = SingleRow(
            ("a", DataValue.FromInt64(1_000_000L)),
            ("b", DataValue.FromFloat64(0.25)));
        List<Row> rows = await ExecuteQueryAsync("SELECT a + b AS r FROM data", catalog);

        Assert.Equal(DataKind.Float64, rows[0]["r"].Kind);
        Assert.Equal(1_000_000.25, rows[0]["r"].AsFloat64());
    }

    [Fact]
    public async Task Float32_Plus_Float64_PromotesToFloat64()
    {
        TableCatalog catalog = SingleRow(
            ("a", DataValue.FromFloat32(1.5f)),
            ("b", DataValue.FromFloat64(2.25)));
        List<Row> rows = await ExecuteQueryAsync("SELECT a + b AS r FROM data", catalog);

        Assert.Equal(DataKind.Float64, rows[0]["r"].Kind);
        Assert.Equal(3.75, rows[0]["r"].AsFloat64());
    }

    // ───────────────────── Decimal precedence ─────────────────────

    [Fact]
    public async Task Decimal_Plus_Int_StaysDecimal()
    {
        TableCatalog catalog = SingleRow(
            ("a", DataValue.FromDecimal(1.25m)),
            ("b", DataValue.FromInt32(2)));
        List<Row> rows = await ExecuteQueryAsync("SELECT a + b AS r FROM data", catalog);

        Assert.Equal(DataKind.Decimal, rows[0]["r"].Kind);
        Assert.Equal(3.25m, rows[0]["r"].AsDecimal());
    }

    [Fact]
    public async Task Decimal_Times_Decimal_StaysDecimal()
    {
        TableCatalog catalog = SingleRow(
            ("a", DataValue.FromDecimal(2.5m)),
            ("b", DataValue.FromDecimal(4.0m)));
        List<Row> rows = await ExecuteQueryAsync("SELECT a * b AS r FROM data", catalog);

        Assert.Equal(DataKind.Decimal, rows[0]["r"].Kind);
        Assert.Equal(10.0m, rows[0]["r"].AsDecimal());
    }

    // ───────────────────── Int128 preservation ─────────────────────

    [Fact]
    public async Task Int128_Plus_Int128_StaysInt128()
    {
        // Used to throw "Cannot use Int128 in arithmetic"; now stays
        // in Int128 throughout the operation.
        TableCatalog catalog = SingleRow(
            ("a", DataValue.FromInt128((Int128)long.MaxValue + 100)),
            ("b", DataValue.FromInt128(50)));
        List<Row> rows = await ExecuteQueryAsync("SELECT a + b AS r FROM data", catalog);

        Assert.Equal(DataKind.Int128, rows[0]["r"].Kind);
        Assert.Equal((Int128)long.MaxValue + 150, rows[0]["r"].AsInt128());
    }

    [Fact]
    public async Task Int128_Plus_Int32_StaysInt128()
    {
        TableCatalog catalog = SingleRow(
            ("a", DataValue.FromInt128(1000)),
            ("b", DataValue.FromInt32(7)));
        List<Row> rows = await ExecuteQueryAsync("SELECT a + b AS r FROM data", catalog);

        Assert.Equal(DataKind.Int128, rows[0]["r"].Kind);
        Assert.Equal((Int128)1007, rows[0]["r"].AsInt128());
    }

    // ───────────────────── Divide / Power always float ─────────────────────

    [Fact]
    public async Task Int_Divide_Int_ReturnsFloatNotIntegerTruncation()
    {
        // SQL ergonomics: 5 / 2 → 2.5, NOT 2. Users who want truncated
        // integer division can cast first.
        TableCatalog catalog = SingleRow(
            ("a", DataValue.FromInt32(5)),
            ("b", DataValue.FromInt32(2)));
        List<Row> rows = await ExecuteQueryAsync("SELECT a / b AS r FROM data", catalog);

        Assert.True(rows[0]["r"].Kind is DataKind.Float32 or DataKind.Float64);
        // Read through Float32 since the promotion target for Int32 / Int32
        // is Float32 (no Float64 operand to upgrade it).
        Assert.Equal(2.5f, rows[0]["r"].AsFloat32());
    }

    [Fact]
    public async Task Float64_Divide_Int_PromotesToFloat64()
    {
        TableCatalog catalog = SingleRow(
            ("a", DataValue.FromFloat64(7.0)),
            ("b", DataValue.FromInt32(2)));
        List<Row> rows = await ExecuteQueryAsync("SELECT a / b AS r FROM data", catalog);

        Assert.Equal(DataKind.Float64, rows[0]["r"].Kind);
        Assert.Equal(3.5, rows[0]["r"].AsFloat64());
    }

    [Fact]
    public async Task Int_Power_Int_ReturnsFloat32()
    {
        // Power goes through MathF.Pow / Math.Pow regardless of operand
        // kinds — never an integer result.
        TableCatalog catalog = SingleRow(
            ("a", DataValue.FromInt32(2)),
            ("b", DataValue.FromInt32(10)));
        List<Row> rows = await ExecuteQueryAsync("SELECT a ^ b AS r FROM data", catalog);

        Assert.Equal(DataKind.Float32, rows[0]["r"].Kind);
        Assert.Equal(1024f, rows[0]["r"].AsFloat32());
    }

    // ───────────────────── Unary negate preserves kind ─────────────────────

    [Fact]
    public async Task UnaryNegate_Int64_StaysInt64()
    {
        TableCatalog catalog = SingleRow(
            ("a", DataValue.FromInt64(42L)));
        List<Row> rows = await ExecuteQueryAsync("SELECT -a AS r FROM data", catalog);

        Assert.Equal(DataKind.Int64, rows[0]["r"].Kind);
        Assert.Equal(-42L, rows[0]["r"].AsInt64());
    }

    [Fact]
    public async Task UnaryNegate_Decimal_StaysDecimal()
    {
        TableCatalog catalog = SingleRow(
            ("a", DataValue.FromDecimal(3.14m)));
        List<Row> rows = await ExecuteQueryAsync("SELECT -a AS r FROM data", catalog);

        Assert.Equal(DataKind.Decimal, rows[0]["r"].Kind);
        Assert.Equal(-3.14m, rows[0]["r"].AsDecimal());
    }

    // ───────────────────── End-to-end procedural use case ─────────────────────

    [Fact]
    public async Task ForIn_AccumulateInt64_StaysInt64Throughout()
    {
        // The user's exact scenario: summing INT64 file sizes through a
        // FOR-IN loop. Before kind-promoted arithmetic, sum would silently
        // become Float32 after the first `+`, losing precision past
        // ~10⁷ bytes. This pins the fix end-to-end.
        TableCatalog catalog = CreateCatalog("files",
            columns: ["size"],
            [DataValue.FromInt64(2_500_000_001L)],
            [DataValue.FromInt64(3_500_000_002L)]);

        IReadOnlyList<DatumIngest.Parsing.Ast.Statement> stmts = DatumIngest.Parsing.SqlParser.ParseBatch(
            "DECLARE sum INT64 = 0 " +
            "FOR row IN (SELECT size FROM files) " +
            "  SET sum = sum + row['size']");

        DatumIngest.Execution.BatchExecutor exec = new(catalog);
        DatumIngest.Execution.BatchResult result = await exec.ExecuteAsync(stmts, CancellationToken.None);

        // Convert.ToInt64 round-trips correctly only if sum was bound as
        // an integer kind through the whole loop — Float32 would have
        // dropped precision below the asserted exact total.
        Assert.Equal(6_000_000_003L, Convert.ToInt64(result.FinalBindings["sum"]));
    }
}
